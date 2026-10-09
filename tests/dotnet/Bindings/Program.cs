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
    var uniforms = new Dictionary<string, object>
    {
        ["matrix"] = matrix,
        ["gain"] = .25f,
        ["index"] = 7,
        ["色"] = new List<float> { 1, .5f, .75f, 1 },
        ["empty"] = Array.Empty<float>()
    };
    var bindings = new Dictionary<string, object>
    {
        ["image"] = new TextureRef(11),
        ["uniforms"] = uniforms,
        ["vertices"] = new BufferRef(22)
    };
    var arena = LubRuntime.Arena.Begin();
    try
    {
        var p = arena.Bindings(bindings, out int count);
        Check(count == 7, "Flattened binding count");
        Check(Name(p[0]) == "image" && p[0].handle == 11 && p[0].count == 0 && p[0].values == null, "Texture binding");
        Check(Name(p[1]) == "matrix" && p[1].handle == 0 && p[1].count == 16, "Matrix binding layout");
        Check(new ReadOnlySpan<float>(p[1].values, 16).SequenceEqual(matrix), "Matrix value order");
        Check(Name(p[2]) == "gain" && p[2].count == 1 && p[2].values[0] == .25f, "Float scalar");
        Check(Name(p[3]) == "index" && p[3].count == 1 && p[3].values[0] == 7, "Integer scalar");
        Check(Name(p[4]) == "色" && p[4].count == 4 && p[4].values[2] == .75f, "UTF-8 name and float list");
        Check(Name(p[5]) == "empty" && p[5].count == 0 && p[5].values == null, "Empty uniform");
        Check(Name(p[6]) == "vertices" && p[6].handle == 22 && p[6].values == null, "Buffer following uniforms");

        var nested = LubRuntime.Arena.Begin();
        try { nested.Bindings(new() { ["uniforms"] = new Dictionary<string, object> { ["large"] = new float[40000] } }, out _); }
        finally { nested.End(); }
        Check(Name(p[0]) == "image" && p[1].values[15] == matrix[15], "Bindings survive nested arena growth");
        Check(arena.Bindings(null, out count) == null && count == 0, "Null bindings");
        arena.Bindings(new(), out count);
        Check(count == 0, "Empty bindings");
        arena.Bindings(new() { ["uniforms"] = new Dictionary<string, object>() }, out count);
        Check(count == 0, "Empty uniform dictionary");
        foreach (var invalid in new[] {
            new Dictionary<string, object> { ["image"] = 42 },
            new Dictionary<string, object> { ["uniforms"] = new Dictionary<string, object> { ["gain"] = "wrong" } }
        })
        {
            bool rejected = false;
            try { arena.Bindings(invalid, out _); }
            catch (LubException) { rejected = true; }
            Check(rejected, "Invalid binding must throw");
        }
    }
    finally { arena.End(); }

    for (int i = 0; i < 100; i++)
    {
        arena = LubRuntime.Arena.Begin();
        try { arena.Bindings(bindings, out _); }
        finally { arena.End(); }
    }
    long allocated = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 1000; i++)
    {
        arena = LubRuntime.Arena.Begin();
        try { arena.Bindings(bindings, out _); }
        finally { arena.End(); }
    }
    Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "Warmed binding marshalling must not allocate managed objects");
    Console.WriteLine("PASS binding layout, values, errors, nested lifetime and zero managed allocation");
}
