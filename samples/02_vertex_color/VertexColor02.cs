// lub の samples/02_vertex_color (Haxe 版 VertexColor02.hx) の TinyC# 版 entry。
// 実行: lub samples/02_vertex_color/VertexColor02.csproj (transpile + watch + hot reload)
using System;
using System.Collections.Generic;

public static class VertexColor02
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

    public static void onFrame(float dt)
    {
        Io.load_text("samples/02_vertex_color/data/02_vcol.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.load_text("samples/02_vertex_color/data/02_vcol.fs.slang",
            out var fs, out var fsv, out _, out _);
        Io.load_floats("samples/02_vertex_color/data/02_vcol.verts.lua",
            out var verts, out var vv, out _, out _);
        if (vs == null || fs == null || verts == null) return;

        var shader = Gfx.use_shader("vc_shader", vs, fs, vsv * 31 + fsv);
        var vbuf = Gfx.use_buffer("vc_verts", Gfx.VERTEX, verts, vv);
        if (shader == null || vbuf == null) return;

        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new float[] { 0.1f, 0.1f, 0.2f, 1.0f },
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
