// Entry of lub's samples/03_texture.
// Run: lub samples/03_texture/Texture03.csproj (transpile + watch + hot reload)
using System;
using System.Collections.Generic;
using Lub;

public static class Texture03
{
    // pos.xyz, pad0, uv.xy, pad1.xy
    static List<float> verts = new List<float>
    {
         0.0f,  0.7f, 0.0f, 0.0f, 0.5f, 0.0f, 0.0f, 0.0f,
        -0.7f, -0.7f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f,
         0.7f, -0.7f, 0.0f, 0.0f, 1.0f, 1.0f, 0.0f, 0.0f,
    };

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        App.Config(new ConfigOpts { Backend = backend });
    }

    public static void OnFrame(float dt)
    {
        Io.LoadText("samples/03_texture/data/03_tex.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.LoadText("samples/03_texture/data/03_tex.fs.slang",
            out var fs, out var fsv, out _, out _);
        Png.Load("samples/03_texture/data/03_tex.png",
            out var px, out var w, out var h, out var fmt, out _, out var pv,
            out _, out _);
        if (vs == null || fs == null || px == null) return;

        var s = Gfx.UseShader("tex_shader", vs, fs, vsv * 31 + fsv);
        var b = Gfx.UseBuffer("tex_verts", Gfx.BufferType.Storage, verts);
        var t = Gfx.UseTextureBytes("tex_chk", w, h, (Gfx.PixelFormat)fmt, px, pv);
        if (s == null || b == null || t == null) return;

        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.1f, 0.1f, 0.2f, 1.0f },
        });
        var bindings = new Bindings();
        bindings.Set("verts", b);
        bindings.Set("diffuse", t);
        Gfx.Draw(3, bindings,
            new DrawOpts
            {
                Shader = s,
                Depth = false,
                Cull = Gfx.Cull.None,
            });
        Gfx.EndPass();
    }
}
