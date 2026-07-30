// lub の samples/06_deferred (Haxe 版 Deferred06.hx) の TinyC# 版 entry。
// 実行: lub samples/06_deferred/Deferred06.csproj (transpile + watch + hot reload)
// G-buffer pass (MRT: SV_Target0 -> gbuf0, SV_Target1 -> gbuf1) の後、
// view pass で左半分に gbuf0、右半分に gbuf1 を貼って可視化する。
using System;
using System.Collections.Generic;

public static class Deferred06
{
    const int RT_W = 256;
    const int RT_H = 256;

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
        Io.load_text("samples/06_deferred/data/06_gbuffer.vs.slang",
            out var gvs, out var gvsv, out _, out _);
        Io.load_text("samples/06_deferred/data/06_gbuffer.fs.slang",
            out var gfs, out var gfsv, out _, out _);
        Io.load_floats("samples/06_deferred/data/06_gbuffer.verts.lua",
            out var gverts, out var gvv, out _, out _);
        Io.load_text("samples/06_deferred/data/06_view.vs.slang",
            out var vvs, out var vvsv, out _, out _);
        Io.load_text("samples/06_deferred/data/06_view.fs.slang",
            out var vfs, out var vfsv, out _, out _);
        Io.load_floats("samples/06_deferred/data/06_view.verts.lua",
            out var vverts, out var vvv, out _, out _);
        if (gvs == null || gfs == null || gverts == null
            || vvs == null || vfs == null || vverts == null)
        {
            return;
        }

        // G-buffer attachments. Two render-target textures of the same size.
        var gbuf0 = Gfx.use_texture("gbuf0", RT_W, RT_H, Gfx.RGBA8, null, 1,
            new TextureOpts { filter = Gfx.LINEAR, wrap = Gfx.CLAMP, target = true });
        var gbuf1 = Gfx.use_texture("gbuf1", RT_W, RT_H, Gfx.RGBA8, null, 1,
            new TextureOpts { filter = Gfx.LINEAR, wrap = Gfx.CLAMP, target = true });
        if (gbuf0 == null || gbuf1 == null) return;

        // G-buffer pass: MRT write. SV_Target0 -> gbuf0, SV_Target1 -> gbuf1.
        var shG = Gfx.use_shader("gbuf_shader", gvs, gfs, gvsv * 31 + gfsv);
        var bG = Gfx.use_buffer("gbuf_verts", Gfx.VERTEX, gverts, gvv);
        if (shG == null || bG == null) return;
        Gfx.begin_pass(new PassOpts
        {
            targets = new List<TextureRef> { gbuf0, gbuf1 },
            clear_colors = new List<double[]>
            {
                new double[] { 0.1, 0.1, 0.15, 1.0 },
                new double[] { 0.15, 0.1, 0.1, 1.0 },
            },
        });
        Gfx.draw(3,
            new Dictionary<string, object> { ["verts"] = bG },
            new DrawOpts { shader = shG, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();

        // View pass: split-screen visualization. Left half samples gbuf0,
        // right half samples gbuf1. Same shader; only the texture binding and
        // the (scale, offset) transform uniform differ.
        var shV = Gfx.use_shader("view_shader", vvs, vfs, vvsv * 31 + vfsv);
        var bV = Gfx.use_buffer("view_verts", Gfx.VERTEX, vverts, vvv);
        if (shV == null || bV == null) return;
        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new double[] { 0.0, 0.0, 0.0, 1.0 },
        });
        Gfx.draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = bV,
                ["gbuf"] = gbuf0,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["transform"] = new double[] { 0.5, 1.0, -0.5, 0.0 },
                },
            },
            new DrawOpts { shader = shV, depth = false, cull = Gfx.NONE });
        Gfx.draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = bV,
                ["gbuf"] = gbuf1,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["transform"] = new double[] { 0.5, 1.0, 0.5, 0.0 },
                },
            },
            new DrawOpts { shader = shV, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }
}
