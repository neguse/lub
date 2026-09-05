// lub の samples/01_triangle の entry。
// 実行: lub samples/01_triangle/Triangle01.csproj (transpile + watch + hot reload)
using System;
using System.Collections.Generic;
using static Lub;

public static class Triangle01
{
    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        Lub.Config(new ConfigOpts { Backend = backend });
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    public static void OnFrame(float dt)
    {
        Io.LoadText("samples/01_triangle/data/01_triangle.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.LoadText("samples/01_triangle/data/01_triangle.fs.slang",
            out var fs, out var fsv, out _, out _);
        Io.LoadFloats("samples/01_triangle/data/01_triangle.verts.lua",
            out var verts, out var vv, out _, out _);
        if (vs == null || fs == null || verts == null) return;

        var shader = Gfx.UseShader("tri_shader", vs, fs, vsv * 31 + fsv);
        var vbuf = Gfx.UseBuffer("tri_verts", Gfx.BufferType.Vertex, verts, vv);
        if (shader == null || vbuf == null) return;

        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.1f, 0.1f, 0.2f, 1.0f },
        });
        Gfx.Draw(3,
            new Dictionary<string, object> { ["verts"] = vbuf },
            new DrawOpts
            {
                Shader = shader,
                Depth = false,
                Cull = Gfx.Cull.None,
            });
        Gfx.EndPass();
    }
}
