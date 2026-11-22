// Fetches 4 vertices from a StructuredBuffer by SV_VertexID (vertex pulling).
// Producing the same image as TestIndexedDraw without a vertex buffer or input layout verifies it.
// Pass/fail is decided by the rendering golden (tests/golden/TestVertexPull_*.png).
using System;
using System.Collections.Generic;
using Lub;

public static class TestVertexPull
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    // lub export draws 1 frame and records the shaders and files used
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
        var bindings = new Bindings();
        bindings.Set("verts", sb!);
        Gfx.Draw(6, bindings,
            new DrawOpts { Shader = s!, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
