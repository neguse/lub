// lub の samples/01_triangle (Haxe 版 Triangle01.hx) の TinyC# 版 entry。
// 実行: lub samples/01_triangle/Triangle01.csproj (transpile + watch + hot reload)
using System;
using System.Collections.Generic;

public static class Triangle01
{
    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND");
        Lub.config(new ConfigOpts { backend = backend });
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    public static void onFrame(double dt)
    {
        Io.load_text("samples/01_triangle/data/01_triangle.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.load_text("samples/01_triangle/data/01_triangle.fs.slang",
            out var fs, out var fsv, out _, out _);
        Io.load_floats("samples/01_triangle/data/01_triangle.verts.lua",
            out var verts, out var vv, out _, out _);
        if (vs == null || fs == null || verts == null) return;

        var shader = Gfx.use_shader("tri_shader", vs, fs, vsv * 31 + fsv);
        var vbuf = Gfx.use_buffer("tri_verts", Gfx.VERTEX, verts, vv);
        if (shader == null || vbuf == null) return;

        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new double[] { 0.1, 0.1, 0.2, 1.0 },
        });
        Gfx.draw(3,
            new Dictionary<string, object> { ["verts"] = vbuf },
            new DrawOpts
            {
                shader = shader,
                depth = false,
                cull = Gfx.NONE,
            });
        Gfx.end_pass();
    }
}
