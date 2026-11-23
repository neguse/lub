// Dispatches a compute shader with a uniform block with 2 groups (writing markers to mark) and 1 group (writing values to val), visualizes both storage buffers with a draw, and reads them back.
// A dispatch without uniforms is an error.
// Expected: the center pixel is (128, 128, 255).
using System;
using System.Collections.Generic;
using Lub;

public static class TestComputeUniformBlock
{
    static Readback? rb;
    static int frames;
    static bool requested;
    static bool omitted;

    static readonly List<float> zeros = new List<float> { 0, 0, 0, 0, 0, 0, 0, 0 };

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("compute_uniform_block");
    }

    // lub export draws 1 frame and records the shaders and files used (the dispatch that checks the error is not run)
    public static void OnExport()
    {
        omitted = true;
        OnFrame(0);
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "test_compute_uniform_block: readback never became ready");

        Io.LoadText("tests/cs/data/test_compute_uniform_block.cs.slang", out var cs, out var verCs, out _, out _);
        Io.LoadText("tests/cs/data/test_compute_uniform_block.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_compute_uniform_block.fs.slang", out var fs, out var verFs, out _, out _);
        if (cs == null || vs == null || fs == null)
            return;
        var shC = Gfx.UseShaderCompute("cs", cs, verCs);
        var shR = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        var mark = Gfx.UseBuffer("mark", Gfx.BufferType.Storage, zeros, 1);
        var val = Gfx.UseBuffer("val", Gfx.BufferType.Storage, zeros, 1);
        var rt = Gfx.UseTexture("rt", 4, 4, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp });
        if (shC == null || shR == null || mark == null || val == null || rt == null) return;

        var bindings = new Bindings();
        bindings.Set("out_buf", mark);
        if (!omitted)
        {
            omitted = true;
            Check.Error("a dispatch without the uniform block must fail", "missing 'value'");
            Gfx.Dispatch(2, 1, 1, bindings, new DispatchOpts { Shader = shC });
        }
        // tid 1 writes the marker (the value written by tid 0 is not read).
        bindings.Set("value", new Vec4(0, 0, 0, 0));
        Gfx.Dispatch(2, 1, 1, bindings,
            new DispatchOpts { Shader = shC });
        // tid 0 writes value.
        bindings.Clear();
        bindings.Set("out_buf", val);
        bindings.Set("value", new Vec4(0, 0.5f, 1, 0));
        Gfx.Dispatch(1, 1, 1, bindings,
            new DispatchOpts { Shader = shC });

        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        bindings.Clear();
        bindings.Set("mark", mark);
        bindings.Set("val", val);
        Gfx.Draw(6, bindings,
            new DrawOpts { Shader = shR, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        int? req = requested ? null : 1;
        requested = true;
        Gfx.ReadTexture(rb!, rt, req, out var st, out var bytes, out var w, out var h, out _,
            out var stride, out var id, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready)
            return;
        Check.That(id == 1 && w == 4 && h == 4, "unexpected readback result");
        var off = 2 * stride + 2 * 4;
        var r = bytes!.Get(off);
        var g = bytes.Get(off + 1);
        var b = bytes.Get(off + 2);
        Check.That(Math.Abs(r - 128) <= 3 && Math.Abs(g - 128) <= 3 && Math.Abs(b - 255) <= 3,
            $"test_compute_uniform_block: pixel ({r}, {g}, {b}), want (128, 128, 255)");
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
