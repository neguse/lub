// BeginPass の Load = Gfx.LoadAction.Load が color と depth の両方を保持することの visual test。
// pass 1: 赤クリア + 左に緑 quad (z=0.4)。
// pass 2: Load = LoadAction.Load で中央に大きな青 quad (z=0.8, depth test あり)。
// 期待: 背景の赤が残り (color load)、緑 quad が青 quad を遮る (depth load)。
// 合否は描画の golden (tests/golden/TestLoadOp_*.png) で決まる。
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
        Gfx.Draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = green!,
                ["uniforms"] = new Dictionary<string, object> { ["zcol"] = new float[] { 0.4f, 0.0f, 1.0f, 0.0f } },
            },
            new DrawOpts { Shader = s!, Depth = true, Cull = Gfx.Cull.None });
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        Gfx.Draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = blue!,
                ["uniforms"] = new Dictionary<string, object> { ["zcol"] = new float[] { 0.8f, 0.0f, 0.0f, 1.0f } },
            },
            new DrawOpts { Shader = s!, Depth = true, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
