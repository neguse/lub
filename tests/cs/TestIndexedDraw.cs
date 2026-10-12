// 4 頂点 quad を 6 index で indexed draw する最小テスト。
// vertex 重複なしで quad を成立させられることが indexed draw の動作確認になる。
// 合否は描画の golden (tests/golden/test_indexed_draw_*.png) で決まる。
using System;
using System.Collections.Generic;
using Lub;

public static class TestIndexedDraw
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    public static void OnFrame(float dt)
    {
        Io.LoadText("tests/cs/data/test_indexed_draw.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_indexed_draw.fs.slang", out var fs, out var verFs, out _, out _);
        if (vs == null || fs == null)
            return;
        var s = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);

        var verts = new List<float> { -0.6f, -0.6f, 0.6f, -0.6f, 0.6f, 0.6f, -0.6f, 0.6f };
        var vb = Gfx.UseBuffer("vb", Gfx.BufferType.Storage, verts, 1);
        var indices = new List<int> { 0, 1, 2, 0, 2, 3 };
        var ib = Gfx.UseBufferInts("ib", Gfx.BufferType.Index, indices, 1);

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.05f, 0.05f, 0.1f, 1 } });
        Gfx.Draw(6,
            new Dictionary<string, object> { ["verts"] = vb!, ["indices"] = ib! },
            new DrawOpts { Shader = s!, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
