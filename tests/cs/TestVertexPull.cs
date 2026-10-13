// 4 頂点を StructuredBuffer から SV_VertexID で引く (vertex pulling)。
// 頂点 buffer と入力レイアウトを使わずに test_indexed_draw と同じ絵になる
// ことが確認になる。
// 合否は描画の golden (tests/golden/TestVertexPull_*.png) で決まる。
using System;
using System.Collections.Generic;
using Lub;

public static class TestVertexPull
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    // export (native-gate.sh) は 1 frame 分を描いて shader と file を記録する
    public static void OnExport() { OnFrame(0); }

    public static void OnFrame(float dt)
    {
        Io.LoadText("tests/cs/data/test_vertex_pull.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_indexed_draw.fs.slang", out var fs, out var verFs, out _, out _);
        if (vs == null || fs == null)
            return;
        var s = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);

        var verts = new List<float> { -0.6f, -0.6f, 0.6f, -0.6f, 0.6f, 0.6f, -0.6f, 0.6f };
        var sb = Gfx.UseBuffer("sb", Gfx.BufferType.Storage, verts, 1);

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.05f, 0.05f, 0.1f, 1 } });
        Gfx.Draw(6,
            new Dictionary<string, object> { ["verts"] = sb! },
            new DrawOpts { Shader = s!, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
