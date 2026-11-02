// float[] の先頭 count 個から宣言した buffer が、その count 個だけを持つこと。
// 配列は count より長くてよく、同じ version の宣言は data を読まず、count が配列を超えると error になる。
using System;
using Lub;

public static class TestBufferArray
{
    static readonly float[] left = new float[] { 1, 0, 0, 1, 9, 9, 9, 9 };
    static readonly float[] right = new float[] { 0, 1, 0, 1 };
    static Readback? rb;
    static int frames;
    static bool requested;
    static bool readDone;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("buffer_array");
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        if (readDone)
        {
            Check.Pass();
            return;
        }
        frames++;
        Check.That(frames < 300, "test_buffer_array: readback never became ready");
        Io.LoadText("tests/cs/data/test_buffer_recording_order.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_buffer_recording_order.fs.slang", out var fs, out var verFs, out _, out _);
        if (vs == null || fs == null)
            return;
        var sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs)!;
        var opts = new DrawOpts { Shader = sh, Depth = false, Cull = Gfx.Cull.None };
        var rt = Gfx.UseTexture("rt", 4, 4, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp })!;

        var a = Gfx.UseBufferArray("a", Gfx.BufferType.Storage, left, 4, 1)!;
        var b = Gfx.UseBufferArray("b", Gfx.BufferType.Storage, requested ? null : right, 4, 1)!;
        Check.That(a.Version == 1 && b.Version == 1, "the declared version must be kept");

        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        var bindings = new Bindings();
        bindings.Set("col", a);
        bindings.Set("side", new Vec4(0, 0, 0, 0));
        Gfx.Draw(6, bindings, opts);
        bindings.Set("col", b);
        bindings.Set("side", new Vec4(1, 0, 0, 0));
        Gfx.Draw(6, bindings, opts);
        Gfx.EndPass();
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        int? req = requested ? null : 1;
        Gfx.ReadTexture(rb!, rt, req, out var st, out var bytes, out _, out _, out _,
            out var stride, out _, out _, out var err);
        requested = true;
        Check.That(st != Gfx.ReadbackStatus.Error, "read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready)
            return;
        Check.That(bytes!.Get(stride + 4) == 255 && bytes.Get(stride + 5) == 0, "left must be red");
        Check.That(bytes.Get(stride + 8) == 0 && bytes.Get(stride + 9) == 255, "right must be green");

        readDone = true;
        Check.Error("a count past the end of the array must fail", "out of range");
        Gfx.UseBufferArray("c", Gfx.BufferType.Storage, right, 5);
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
