// lub の samples/03_texture (Haxe 版 Texture03.hx) の TinyC# 版 entry。
// 実行: lub samples/03_texture/Texture03.csproj (transpile + watch + hot reload)
using System.Collections.Generic;

public static class Texture03
{
    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND") ?? "native";
        Lub.config(new ConfigOpts { backend = backend });
    }

    public static void onFrame(double dt)
    {
        Io.load_text("samples/03_texture/data/03_tex.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.load_text("samples/03_texture/data/03_tex.fs.slang",
            out var fs, out var fsv, out _, out _);
        Io.load_floats("samples/03_texture/data/03_tex.verts.lua",
            out var verts, out var vv, out _, out _);
        Png.load("samples/03_texture/data/03_tex.png",
            out var px, out var w, out var h, out var fmt, out _, out var pv,
            out _, out _);
        if (vs == null || fs == null || verts == null || px == null) return;

        var s = Gfx.use_shader("tex_shader", vs, fs, vsv * 31 + fsv);
        var b = Gfx.use_buffer("tex_verts", Gfx.VERTEX, verts, vv);
        var t = Gfx.use_texture("tex_chk", w, h, fmt, px, pv);
        if (s == null || b == null || t == null) return;

        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new double[] { 0.1, 0.1, 0.2, 1.0 },
        });
        Gfx.draw(3,
            new Dictionary<string, object> { ["verts"] = b, ["diffuse"] = t },
            new DrawOpts
            {
                shader = s,
                depth = false,
                cull = Gfx.NONE,
            });
        Gfx.end_pass();
    }
}
