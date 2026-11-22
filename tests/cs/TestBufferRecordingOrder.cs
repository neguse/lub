// Within one frame, redeclares the buffer with the same key with different contents, omitting version, and draws after each declaration.
// A draw reads the contents in the order the declarations were recorded, so in every case the left half is drawn with the contents before the redeclaration and the right half with the contents after it: the left is red and the right is green.
// On a backend where writes overtake the recording order, the left is also drawn with the later contents.
// - storage: rewrites the STORAGE buffer the draw reads.
// - index: rewrites the INDEX buffer.
//   The earlier index points to the left quad, and the later one
//   to the right quad.
// - compute: after the draw, the CPU rewrites with green the buffer that compute wrote red into.
// - resize: redeclares with contents of a different size, recreating the buffer the draw used.
using System;
using System.Collections.Generic;
using Lub;

public static class TestBufferRecordingOrder
{
    static readonly string[] cases = new string[] { "storage", "index", "compute", "resize" };
    static readonly List<Readback> rbs = new List<Readback>();
    static readonly bool[] done = new bool[4];
    static readonly List<string> mismatches = new List<string>();
    static int frames;
    static bool requested;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        for (var i = 0; i < 4; i++)
            rbs.Add(Gfx.Readback("buffer_recording_order_" + cases[i])!);
    }

    static List<int> QuadIndices(int first)
    {
        var t = new List<int>();
        for (var i = 0; i < 6; i++)
            t.Add(first + i);
        return t;
    }

    static Bindings Col(BufferRef col, float side)
    {
        var bindings = new Bindings();
        bindings.Set("col", col);
        bindings.Set("side", new Vec4(side, 0, 0, 0));
        return bindings;
    }

    static Bindings Indexed(BufferRef idx, BufferRef col)
    {
        var bindings = Col(col, 0);
        bindings.Set("indices", idx);
        return bindings;
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "test_buffer_recording_order: readback never became ready");

        Io.LoadText("tests/cs/data/test_buffer_recording_order.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_buffer_recording_order.fs.slang", out var fs, out var verFs, out _, out _);
        Io.LoadText("tests/cs/data/test_buffer_recording_order.cs.slang", out var cs, out var verCs, out _, out _);
        if (vs == null || fs == null || cs == null)
            return;
        var sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        var shC = Gfx.UseShaderCompute("cs", cs, verCs);
        if (sh == null || shC == null) return;
        var opts = new DrawOpts { Shader = sh, Depth = false, Cull = Gfx.Cull.None };
        var red = Gfx.UseBuffer("red", Gfx.BufferType.Storage, new List<float> { 1, 0, 0, 1 }, 1)!;
        var green = Gfx.UseBuffer("green", Gfx.BufferType.Storage, new List<float> { 0, 1, 0, 1 }, 1)!;

        var rtOpts = new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        var rts = new List<TextureRef>();
        for (var i = 0; i < 4; i++)
            rts.Add(Gfx.UseTexture("rt_" + cases[i], 4, 4, Gfx.PixelFormat.Rgba8, null, 1, rtOpts)!);

        Gfx.BeginPass(new PassOpts { Target = rts[0], ClearColor = new float[] { 0, 0, 0, 1 } });
        var col = Gfx.UseBuffer("col", Gfx.BufferType.Storage, new List<float> { 1, 0, 0, 1 })!;
        Gfx.Draw(6, Col(col, 0), opts);
        col = Gfx.UseBuffer("col", Gfx.BufferType.Storage, new List<float> { 0, 1, 0, 1 })!;
        Gfx.Draw(6, Col(col, 1), opts);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = rts[1], ClearColor = new float[] { 0, 0, 0, 1 } });
        var idx = Gfx.UseBufferInts("idx", Gfx.BufferType.Index, QuadIndices(0))!;
        Gfx.Draw(6, Indexed(idx, red), opts);
        idx = Gfx.UseBufferInts("idx", Gfx.BufferType.Index, QuadIndices(6))!;
        Gfx.Draw(6, Indexed(idx, green), opts);
        Gfx.EndPass();

        var outBuf = Gfx.UseBuffer("out", Gfx.BufferType.Storage, new List<float> { 0, 0, 0, 1 })!;
        var bindings = new Bindings();
        bindings.Set("col", outBuf);
        Gfx.Dispatch(1, 1, 1, bindings,
            new DispatchOpts { Shader = shC });
        Gfx.BeginPass(new PassOpts { Target = rts[2], ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.Draw(6, Col(outBuf, 0), opts);
        outBuf = Gfx.UseBuffer("out", Gfx.BufferType.Storage, new List<float> { 0, 1, 0, 1 })!;
        Gfx.Draw(6, Col(outBuf, 1), opts);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = rts[3], ClearColor = new float[] { 0, 0, 0, 1 } });
        var sized = Gfx.UseBuffer("sized", Gfx.BufferType.Storage, new List<float> { 1, 0, 0, 1 })!;
        Gfx.Draw(6, Col(sized, 0), opts);
        sized = Gfx.UseBuffer("sized", Gfx.BufferType.Storage, new List<float> { 0, 1, 0, 1, 0, 0, 0, 0 })!;
        Gfx.Draw(6, Col(sized, 1), opts);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        var allDone = true;
        for (var i = 0; i < 4; i++)
        {
            if (done[i])
                continue;
            int? req = requested ? null : 1;
            Gfx.ReadTexture(rbs[i], rts[i], req, out var st, out var bytes, out var w, out var h, out _,
                out var stride, out var id, out _, out var err);
            Check.That(st != Gfx.ReadbackStatus.Error, cases[i] + ": read_texture failed: " + err);
            if (st == Gfx.ReadbackStatus.Ready)
            {
                Check.That(id == 1 && w == 4 && h == 4, cases[i] + ": unexpected readback result");
                var lr = bytes!.Get(stride + 4);
                var lg = bytes.Get(stride + 5);
                var lb = bytes.Get(stride + 6);
                var rr = bytes.Get(stride + 8);
                var rg = bytes.Get(stride + 9);
                var rb = bytes.Get(stride + 10);
                if (!(lr == 255 && lg == 0 && lb == 0 && rr == 0 && rg == 255 && rb == 0))
                {
                    mismatches.Add($"test_buffer_recording_order {cases[i]}: left ({lr}, {lg}, {lb}) want (255, 0, 0), right ({rr}, {rg}, {rb}) want (0, 255, 0)");
                }
                done[i] = true;
            }
            else
            {
                allDone = false;
            }
        }
        requested = true;
        if (allDone)
        {
            Check.That(mismatches.Count == 0, string.Join("; ", mismatches));
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
