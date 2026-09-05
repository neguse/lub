// lub の samples/03_texture (Haxe 版 Texture03.hx) の TinyC# 版 entry。
// 実行: lub samples/03_texture/Texture03.csproj (transpile + watch + hot reload)
using System.Collections.Generic;
using System;
using static Lub;

public static class Texture03
{
    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "native";
        Lub.Config(new ConfigOpts { Backend = backend });
    }

    public static void OnFrame(double dt)
    {
        Io.LoadText("samples/03_texture/data/03_tex.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.LoadText("samples/03_texture/data/03_tex.fs.slang",
            out var fs, out var fsv, out _, out _);
        Io.LoadFloats("samples/03_texture/data/03_tex.verts.lua",
            out var verts, out var vv, out _, out _);
        Png.Load("samples/03_texture/data/03_tex.png",
            out var px, out var w, out var h, out var fmt, out _, out var pv,
            out _, out _);
        if (vs == null || fs == null || verts == null || px == null) return;

        var s = Gfx.UseShader("tex_shader", vs, fs, vsv * 31 + fsv);
        var b = Gfx.UseBuffer("tex_verts", Gfx.BufferType.Vertex, verts, vv);
        var t = Gfx.UseTexture("tex_chk", w, h, (Gfx.PixelFormat)fmt, px, pv);
        if (s == null || b == null || t == null) return;

        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new double[] { 0.1, 0.1, 0.2, 1.0 },
        });
        Gfx.Draw(3,
            new Dictionary<string, object> { ["verts"] = b, ["diffuse"] = t },
            new DrawOpts
            {
                Shader = s,
                Depth = false,
                Cull = Gfx.Cull.None,
            });
        Gfx.EndPass();
    }
}
