// lub の samples/02_vertex_color (Haxe 版 VertexColor02.hx) の TinyC# 版 entry。
// 実行: lub samples/02_vertex_color/VertexColor02.csproj (transpile + watch + hot reload)
using System;
using System.Collections.Generic;
using static Lub;

public static class VertexColor02
{
    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "native";
        Lub.Config(new ConfigOpts { Backend = backend });
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    public static void OnFrame(double dt)
    {
        Io.LoadText("samples/02_vertex_color/data/02_vcol.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.LoadText("samples/02_vertex_color/data/02_vcol.fs.slang",
            out var fs, out var fsv, out _, out _);
        Io.LoadFloats("samples/02_vertex_color/data/02_vcol.verts.lua",
            out var verts, out var vv, out _, out _);
        if (vs == null || fs == null || verts == null) return;

        var shader = Gfx.UseShader("vc_shader", vs, fs, vsv * 31 + fsv);
        var vbuf = Gfx.UseBuffer("vc_verts", Gfx.BufferType.Vertex, verts, vv);
        if (shader == null || vbuf == null) return;

        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new double[] { 0.1, 0.1, 0.2, 1.0 },
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
