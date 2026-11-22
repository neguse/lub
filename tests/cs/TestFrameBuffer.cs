// However many times FrameBuffer is called within one frame, each draw / dispatch reads its own range.
// In every case the left half is drawn with the earlier range and the right half with the later range: the left is red and the right is green.
// - storage: reuses Bindings and replaces the STORAGE range.
// - index: replaces the INDEX range.
//   The earlier index points to the left quad, and the later one to the right quad.
// - compute: compute reads the FrameBuffer range and copies it into a buffer, and the draw uses that buffer.
// - stride: creates a range with 12-byte elements after a range whose size is not aligned.
// - large: a range larger than a chunk.
// - array: a range created from a float[].
using System;
using System.Collections.Generic;
using Lub;

public static class TestFrameBuffer
{
    const int LargeElements = 400000;
    static readonly string[] cases = new string[] { "storage", "index", "compute", "stride", "large", "array" };
    static readonly List<Readback> rbs = new List<Readback>();
    static readonly bool[] done = new bool[6];
    static readonly List<string> mismatches = new List<string>();
    static readonly float[] redArray = new float[] { 1, 0, 0, 1, 7, 7, 7, 7 };
    static readonly float[] greenArray = new float[] { 0, 1, 0, 1 };
    static readonly Bindings shared = new Bindings();
    static List<float>? largeRed;
    static List<float>? largeGreen;
    static int frames;
    static bool requested;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        for (var i = 0; i < cases.Length; i++)
            rbs.Add(Gfx.Readback("frame_buffer_" + cases[i])!);
        largeRed = Fill(LargeElements, 1, 0);
        largeGreen = Fill(LargeElements, 0, 1);
    }

    static List<float> Fill(int n, float r, float g)
    {
        var data = new List<float>();
        for (var i = 0; i < n; i++)
        {
            data.Add(r);
            data.Add(g);
            data.Add(0);
            data.Add(1);
        }
        return data;
    }

    static List<float> Color(float r, float g)
    {
        return new List<float> { r, g, 0, 1 };
    }

    static List<int> QuadIndices(int first)
    {
        var t = new List<int>();
        for (var i = 0; i < 6; i++)
            t.Add(first + i);
        return t;
    }

    static void Side(DrawOpts opts, FrameRange col, float side)
    {
        shared.Set("col", col);
        shared.Set("side", new Vec4(side, 0, 0, 0));
        Gfx.Draw(6, shared, opts);
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "test_frame_buffer: readback never became ready");

        Io.LoadText("tests/cs/data/test_buffer_recording_order.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_buffer_recording_order.fs.slang", out var fs, out var verFs, out _, out _);
        Io.LoadText("tests/cs/data/test_frame_buffer.cs.slang", out var cs, out var verCs, out _, out _);
        Io.LoadText("tests/cs/data/test_frame_buffer_stride.fs.slang", out var fs3, out var verFs3, out _, out _);
        if (vs == null || fs == null || cs == null || fs3 == null)
            return;
        var sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs)!;
        var sh3 = Gfx.UseShader("sh3", vs, fs3, verVs ^ verFs3)!;
        var shC = Gfx.UseShaderCompute("cs", cs, verCs)!;
        var opts = new DrawOpts { Shader = sh, Depth = false, Cull = Gfx.Cull.None };
        var opts3 = new DrawOpts { Shader = sh3, Depth = false, Cull = Gfx.Cull.None };

        var rtOpts = new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        var rts = new List<TextureRef>();
        for (var i = 0; i < cases.Length; i++)
            rts.Add(Gfx.UseTexture("rt_" + cases[i], 4, 4, Gfx.PixelFormat.Rgba8, null, 1, rtOpts)!);
        var clear = new float[] { 0, 0, 0, 1 };

        Gfx.BeginPass(new PassOpts { Target = rts[0], ClearColor = clear });
        Side(opts, Gfx.FrameBuffer(Gfx.BufferType.Storage, Color(1, 0)), 0);
        Side(opts, Gfx.FrameBuffer(Gfx.BufferType.Storage, Color(0, 1)), 1);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = rts[1], ClearColor = clear });
        var indexed = new Bindings();
        indexed.Set("side", new Vec4(0, 0, 0, 0));
        indexed.Set("col", Gfx.FrameBuffer(Gfx.BufferType.Storage, Color(1, 0)));
        indexed.Set("indices", Gfx.FrameBufferInts(Gfx.BufferType.Index, QuadIndices(0)));
        Gfx.Draw(6, indexed, opts);
        indexed.Set("col", Gfx.FrameBuffer(Gfx.BufferType.Storage, Color(0, 1)));
        indexed.Set("indices", Gfx.FrameBufferInts(Gfx.BufferType.Index, QuadIndices(6)));
        Gfx.Draw(6, indexed, opts);
        Gfx.EndPass();

        var left = Gfx.UseBufferEmpty("out_left", Gfx.BufferType.Storage, 4, 1)!;
        var right = Gfx.UseBufferEmpty("out_right", Gfx.BufferType.Storage, 4, 1)!;
        var dispatch = new Bindings();
        dispatch.Set("src", Gfx.FrameBuffer(Gfx.BufferType.Storage, Color(1, 0)));
        dispatch.Set("dst", left);
        Gfx.Dispatch(1, 1, 1, dispatch, new DispatchOpts { Shader = shC });
        dispatch.Set("src", Gfx.FrameBuffer(Gfx.BufferType.Storage, Color(0, 1)));
        dispatch.Set("dst", right);
        Gfx.Dispatch(1, 1, 1, dispatch, new DispatchOpts { Shader = shC });
        Gfx.BeginPass(new PassOpts { Target = rts[2], ClearColor = clear });
        var drawn = new Bindings();
        drawn.Set("col", left);
        drawn.Set("side", new Vec4(0, 0, 0, 0));
        Gfx.Draw(6, drawn, opts);
        drawn.Set("col", right);
        drawn.Set("side", new Vec4(1, 0, 0, 0));
        Gfx.Draw(6, drawn, opts);
        Gfx.EndPass();

        Gfx.FrameBuffer(Gfx.BufferType.Storage, new List<float> { 0, 0, 0, 0, 0 });
        Gfx.BeginPass(new PassOpts { Target = rts[3], ClearColor = clear });
        var three = new Bindings();
        three.Set("col", Gfx.FrameBuffer(Gfx.BufferType.Storage, new List<float> { 1, 0, 0, 1, 0, 0 }));
        three.Set("side", new Vec4(0, 0, 0, 0));
        Gfx.Draw(6, three, opts3);
        three.Set("col", Gfx.FrameBuffer(Gfx.BufferType.Storage, new List<float> { 0, 1, 0 }));
        three.Set("side", new Vec4(1, 0, 0, 0));
        Gfx.Draw(6, three, opts3);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = rts[4], ClearColor = clear });
        Side(opts, Gfx.FrameBuffer(Gfx.BufferType.Storage, largeRed!), 0);
        Side(opts, Gfx.FrameBuffer(Gfx.BufferType.Storage, largeGreen!), 1);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = rts[5], ClearColor = clear });
        Side(opts, Gfx.FrameBufferArray(Gfx.BufferType.Storage, redArray, 4), 0);
        Side(opts, Gfx.FrameBufferArray(Gfx.BufferType.Storage, greenArray, 4), 1);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = clear });
        Gfx.EndPass();

        var allDone = true;
        for (var i = 0; i < cases.Length; i++)
        {
            if (done[i])
                continue;
            int? req = requested ? null : 1;
            Gfx.ReadTexture(rbs[i], rts[i], req, out var st, out var bytes, out _, out _, out _,
                out var stride, out _, out _, out var err);
            Check.That(st != Gfx.ReadbackStatus.Error, cases[i] + ": read_texture failed: " + err);
            if (st == Gfx.ReadbackStatus.Ready)
            {
                var lr = bytes!.Get(stride + 4);
                var lg = bytes.Get(stride + 5);
                var rr = bytes.Get(stride + 8);
                var rg = bytes.Get(stride + 9);
                if (!(lr == 255 && lg == 0 && rr == 0 && rg == 255))
                    mismatches.Add($"{cases[i]}: left ({lr}, {lg}) want (255, 0), right ({rr}, {rg}) want (0, 255)");
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
