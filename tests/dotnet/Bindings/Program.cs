using System.Text;
using Lub;

unsafe
{
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    static string Name(LubNative.LubBinding binding) =>
        Encoding.UTF8.GetString(new ReadOnlySpan<byte>(binding.name.ptr, binding.name.len));

    var matrix = Enumerable.Range(0, 16).Select(i => i + .5f).ToArray();
    LubRuntime.Arena arena;
    long allocated;
    var typed = new Bindings();
    void Fill()
    {
        Gfx.BindingsSetTexture(typed, "image", new TextureRef(11));
        Gfx.BindingsSetFloatArray(typed, "matrix", matrix);
        Gfx.BindingsSet4(typed, "gain", 1, .25f, 0, 0, 0);
        Gfx.BindingsSetFloats(typed, "色", new List<float> { 1, .5f, .75f, 1 });
        Gfx.BindingsSetBuffer(typed, "vertices", new BufferRef(22));
    }
    Fill();
    arena = LubRuntime.Arena.Begin();
    try
    {
        var p = typed.ToNative(arena, out int count);
        Check(count == 5, "Typed binding count");
        Check(Name(p[0]) == "image" && p[0].handle == 11 && p[0].values == null, "Typed texture");
        Check(Name(p[1]) == "matrix" && p[1].count == 16 && new ReadOnlySpan<float>(p[1].values, 16).SequenceEqual(matrix), "Typed float array");
        Check(Name(p[2]) == "gain" && p[2].count == 1 && p[2].values[0] == .25f, "Typed scalar");
        Check(Name(p[3]) == "色" && p[3].count == 4 && p[3].values[2] == .75f, "Typed UTF-8 name and float list");
        Check(Name(p[4]) == "vertices" && p[4].handle == 22 && p[4].values == null, "Typed buffer");
        // 値は写した時点のもの
        Gfx.BindingsSet4(typed, "gain", 1, .5f, 0, 0, 0);
        Check(p[2].values[0] == .25f, "Later Set must not change the copied values");
        // 同じ名前で handle と uniform を行き来できる
        Gfx.BindingsSet4(typed, "image", 2, 3, 4, 0, 0);
        Gfx.BindingsSetBuffer(typed, "gain", new BufferRef(33));
        var q = typed.ToNative(arena, out count);
        Check(count == 5 && q[0].handle == 0 && q[0].count == 2 && q[0].values[1] == 4, "Handle replaced by a uniform");
        Check(q[2].handle == 33 && q[2].values == null && q[2].count == 0, "Uniform replaced by a handle");
        Gfx.BindingsClear(typed);
        typed.ToNative(arena, out count);
        Check(count == 0, "Clear");
        for (int i = 0; i < 40; i++) Gfx.BindingsSet4(typed, "growing_" + i, 1, i, 0, 0, 0);
        var g = typed.ToNative(arena, out count);
        Check(count == 40 && Name(g[39]) == "growing_39" && g[39].values[0] == 39, "Growth past the first arrays");
        Gfx.BindingsClear(typed);
    }
    finally { arena.End(); }
    Gfx.BindingsSetTexture(typed, "image", new TextureRef(11));
    Gfx.BindingsSet4(typed, "gain", 1, .25f, 0, 0, 0);
    for (int i = 0; i < 100; i++)
    {
        arena = LubRuntime.Arena.Begin();
        try { typed.ToNative(arena, out _); }
        finally { arena.End(); }
    }
    allocated = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 1000; i++)
    {
        Gfx.BindingsSet4(typed, "gain", 1, i, 0, 0, 0);
        arena = LubRuntime.Arena.Begin();
        try { typed.ToNative(arena, out _); }
        finally { arena.End(); }
    }
    Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "Warmed Set and marshalling must not allocate managed objects");

    // bit mask の文字列は 1〜16 桁の hex (0x 付きも可)。
    // 空と 17 桁以上は error (tcs VM と tcs2c の binding と同じ規則)。
    Check(LubRuntime.Bits(null) == 0 && LubRuntime.Bits("0x1F") == 0x1f && LubRuntime.Bits("ffffffffffffffff") == ulong.MaxValue, "Valid bit masks");
    foreach (var (mask, message) in new[] {
        ("", "empty hex string"), ("0x", "empty hex string"),
        ("1ffffffffffffffff", "more than 16 hex digits"), ("12g", "invalid hex"), (" 1", "invalid hex")
    })
    {
        string? error = null;
        try { LubRuntime.Bits(mask); }
        catch (LubException e) { error = e.Message; }
        Check(error != null && error.Contains("bit mask: " + message), $"Bit mask '{mask}' must throw '{message}' (got {error ?? "no error"})");
    }
    Console.WriteLine("PASS binding layout, values, zero managed allocation and bit masks");
}
