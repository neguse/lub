// depth-only pass (color target なし) で Gfx.PixelFormat.Depth32f に三角形を描き、
// その depth texture を通常の texture としてサンプルして可視化する visual test。
// 期待: 三角形領域が暗いグレー (z=0.25)、背景が白 (ClearDepth=1.0)。
// shadow mapping (depth-only pass + depth 直接サンプル) の成立条件そのもの。
// 合否は描画の golden (tests/golden/TestDepthSample_*.png) で決まる。
using System;
using System.Collections.Generic;
using Lub;

public static class TestDepthSample
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    // export (native-gate.sh) は 1 frame 分を描いて shader と file を記録する
    public static void OnExport() { OnFrame(0); }

    public static void OnFrame(float dt)
    {
        Io.LoadText("tests/cs/data/test_depth_sample.vs.slang", out var wvs, out var ver1, out _, out _);
        Io.LoadText("tests/cs/data/test_depth_sample.fs.slang", out var wfs, out var ver2, out _, out _);
        Io.LoadText("tests/cs/data/test_depth_sample_read.vs.slang", out var rvs, out var ver3, out _, out _);
        Io.LoadText("tests/cs/data/test_depth_sample_read.fs.slang", out var rfs, out var ver4, out _, out _);
        if (wvs == null || wfs == null || rvs == null || rfs == null)
            return;
        var writeSh = Gfx.UseShader("sh_w", wvs, wfs, ver1 ^ ver2);
        var readSh = Gfx.UseShader("sh_r", rvs, rfs, ver3 ^ ver4);

        var dt2 = Gfx.UseTexture("dt", 256, 256, Gfx.PixelFormat.Depth32f, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp });

        var tri = Gfx.UseBuffer("vb_tri", Gfx.BufferType.Storage, new List<float> { -0.7f, -0.7f, 0.7f, -0.7f, 0.0f, 0.7f }, 1);
        var quad = Gfx.UseBuffer("vb_quad", Gfx.BufferType.Storage, new List<float> { -1, -1, 1, -1, 1, 1, -1, -1, 1, 1, -1, 1 }, 1);

        Gfx.BeginPass(new PassOpts { DepthTarget = dt2, ClearDepth = 1.0f });
        Gfx.Draw(3,
            new Dictionary<string, object>
            {
                ["verts"] = tri!,
                ["uniforms"] = new Dictionary<string, object> { ["z"] = new float[] { 0.25f, 0, 0, 0 } },
            },
            new DrawOpts { Shader = writeSh!, Depth = true, Cull = Gfx.Cull.None });
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 1, 0, 1, 1 } });
        Gfx.Draw(6,
            new Dictionary<string, object> { ["verts"] = quad!, ["dtex"] = dt2! },
            new DrawOpts { Shader = readSh!, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
