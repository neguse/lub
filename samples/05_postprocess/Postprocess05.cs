// lub の samples/05_postprocess (Haxe 版 Postprocess05.hx) の TinyC# 版 entry。
// 実行: lub samples/05_postprocess/Postprocess05.csproj (transpile + watch + hot reload)
// オフスクリーン RT に三角形を描き、post シェーダで全画面に貼る 2 パス構成。

using System;
using System.Collections.Generic;

public static class Postprocess05
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

    public static void onFrame(float dt)
    {
        Io.load_text("samples/05_postprocess/data/05_offscreen.vs.slang",
            out var ovs, out var ovsv, out _, out _);
        Io.load_text("samples/05_postprocess/data/05_offscreen.fs.slang",
            out var ofs, out var ofsv, out _, out _);
        Io.load_floats("samples/05_postprocess/data/05_offscreen.verts.lua",
            out var overts, out var ovv, out _, out _);
        Io.load_text("samples/05_postprocess/data/05_post.vs.slang",
            out var pvs, out var pvsv, out _, out _);
        Io.load_text("samples/05_postprocess/data/05_post.fs.slang",
            out var pfs, out var pfsv, out _, out _);
        Io.load_floats("samples/05_postprocess/data/05_post.verts.lua",
            out var pverts, out var pvv, out _, out _);
        if (ovs == null || ofs == null || overts == null
            || pvs == null || pfs == null || pverts == null) return;

        var rt = Gfx.use_texture("rt_scene", RT_W, RT_H, Gfx.RGBA8, null, 1,
            new TextureOpts { filter = Gfx.LINEAR, wrap = Gfx.CLAMP, target = true });
        if (rt == null) return;

        var shOff = Gfx.use_shader("off_shader", ovs, ofs, ovsv * 31 + ofsv);
        var bOff = Gfx.use_buffer("off_verts", Gfx.VERTEX, overts, ovv);
        if (shOff == null || bOff == null) return;
        Gfx.begin_pass(new PassOpts
        {
            target = rt,
            clear_color = new float[] { 0.1f, 0.1f, 0.2f, 1.0f },
        });
        Gfx.draw(3,
            new Dictionary<string, object> { ["verts"] = bOff },
            new DrawOpts { shader = shOff, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();

        var shPost = Gfx.use_shader("post_shader", pvs, pfs, pvsv * 31 + pfsv);
        var bPost = Gfx.use_buffer("post_verts", Gfx.VERTEX, pverts, pvv);
        if (shPost == null || bPost == null) return;
        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new float[] { 0.0f, 0.0f, 0.0f, 1.0f },
        });
        Gfx.draw(6,
            new Dictionary<string, object> { ["verts"] = bPost, ["scene"] = rt },
            new DrawOpts { shader = shPost, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }
}
