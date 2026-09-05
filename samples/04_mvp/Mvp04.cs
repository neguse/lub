// lub の samples/04_mvp (Haxe 版 Mvp04.hx) の TinyC# 版 entry。
// 実行: lub samples/04_mvp/Mvp04.csproj (transpile + watch + hot reload)
using System;
using System.Collections.Generic;
using static Lub;

public static class Mvp04
{
    static double t = 0;

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
        t = t + dt;
        Io.LoadText("samples/04_mvp/data/04_mvp.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.LoadText("samples/04_mvp/data/04_mvp.fs.slang",
            out var fs, out var fsv, out _, out _);
        Io.LoadFloats("samples/04_mvp/data/04_mvp.verts.lua",
            out var verts, out var vv, out _, out _);
        if (vs == null || fs == null || verts == null) return;

        var shader = Gfx.UseShader("mvp_shader", vs, fs, vsv * 31 + fsv);
        var vbuf = Gfx.UseBuffer("mvp_verts", Gfx.BufferType.Vertex, verts, vv);
        if (shader == null || vbuf == null) return;

        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new double[] { 0.1, 0.1, 0.2, 1.0 },
        });
        Gfx.Draw(3,
            new Dictionary<string, object>
            {
                ["verts"] = vbuf,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["mvp"] = Mat4.RotateZ(t).M,
                },
            },
            new DrawOpts
            {
                Shader = shader,
                Depth = false,
                Cull = Gfx.Cull.None,
            });
        Gfx.EndPass();
    }
}
