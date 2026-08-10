// lub の samples/07_compute (Haxe 版 Compute07.hx) の TinyC# 版 entry。
// 実行: lub samples/07_compute/Compute07.csproj (transpile + watch + hot reload)
//
// Compute writes 3 vertices (vec4 = position.xy + color.rg) into a storage
// buffer. The render pass then rebinds the same buffer as a VERTEX buffer
// and draws a triangle whose vertex positions/colors came from the GPU.

using System.Collections.Generic;

public static class Compute07
{
    // 3 vertices * 4 floats (vec2 pos + vec2 col) = 12 floats.
    const int VERT_FLOATS = 12;

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND");
        Lub.config(new ConfigOpts { backend = backend });
    }

    public static void onFrame(float dt)
    {
        Io.load_text("samples/07_compute/data/07_gen_verts.cs.slang",
            out var cs, out var csv, out _, out _);
        Io.load_text("samples/07_compute/data/07_render.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.load_text("samples/07_compute/data/07_render.fs.slang",
            out var fs, out var fsv, out _, out _);
        if (cs == null || vs == null || fs == null) return;

        var vbuf = Gfx.use_buffer("compute_verts", Gfx.STORAGE, VERT_FLOATS, 1);
        var shC = Gfx.use_shader_compute("gen", cs, csv);
        var shR = Gfx.use_shader("render", vs, fs, vsv * 31 + fsv);
        if (vbuf == null || shC == null || shR == null) return;

        Gfx.dispatch(1, 1, 1,
            new Dictionary<string, object> { ["out_verts"] = vbuf },
            new DispatchOpts { shader = shC });

        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new float[] { 0.05f, 0.05f, 0.1f, 1.0f },
        });
        Gfx.draw(3,
            new Dictionary<string, object> { ["verts"] = vbuf },
            new DrawOpts { shader = shR, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }
}
