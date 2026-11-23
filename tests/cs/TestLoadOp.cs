// Visual test that Load = Gfx.LoadAction.Load in BeginPass keeps both color and depth.
// pass 1: red clear + green quad on the left (z=0.4).
// pass 2: Load = LoadAction.Load, large blue quad in the center (z=0.8, with depth test).
// Expected: the red background remains (color load), and the green quad occludes the blue quad (depth load).
// Pass/fail is decided by the rendering golden (tests/golden/TestLoadOp_*.png).
using System;
using System.Collections.Generic;
using Lub;

public static class TestLoadOp
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    static List<float> QuadVerts(float x0, float y0, float x1, float y1)
    {
        return new List<float> { x0, y0, x1, y0, x1, y1, x0, y0, x1, y1, x0, y1 };
    }

    // lub export draws 1 frame and records the shaders and files used
    public static void OnExport() { OnFrame(0); }

    public static void OnFrame(float dt)
    {
        Io.LoadText("tests/cs/data/test_load_op.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_load_op.fs.slang", out var fs, out var verFs, out _, out _);
        if (vs == null || fs == null)
            return;
        var s = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);

        var green = Gfx.UseBuffer("vb_green", Gfx.BufferType.Storage, QuadVerts(-0.8f, -0.4f, -0.2f, 0.4f), 1);
        var blue = Gfx.UseBuffer("vb_blue", Gfx.BufferType.Storage, QuadVerts(-0.6f, -0.6f, 0.6f, 0.6f), 1);

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 1, 0, 0, 1 } });
        var bindings = new Bindings();
        bindings.Set("verts", green!);
        bindings.Set("zcol", new Vec4(0.4f, 0.0f, 1.0f, 0.0f));
        Gfx.Draw(6, bindings,
            new DrawOpts { Shader = s!, Depth = true, Cull = Gfx.Cull.None });
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        bindings.Clear();
        bindings.Set("verts", blue!);
        bindings.Set("zcol", new Vec4(0.8f, 0.0f, 0.0f, 1.0f));
        Gfx.Draw(6, bindings,
            new DrawOpts { Shader = s!, Depth = true, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
