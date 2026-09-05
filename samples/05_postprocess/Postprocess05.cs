// lub の samples/05_postprocess の entry。
// 実行: lub samples/05_postprocess/Postprocess05.csproj (transpile + watch + hot reload)
// オフスクリーン RT に三角形を描き、post シェーダで全画面に貼る 2 パス構成。

using System;
using System.Collections.Generic;
using static Lub;

public static class Postprocess05
{
    const int rtW = 256;
    const int rtH = 256;

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
        Io.LoadText("samples/05_postprocess/data/05_offscreen.vs.slang",
            out var ovs, out var ovsv, out _, out _);
        Io.LoadText("samples/05_postprocess/data/05_offscreen.fs.slang",
            out var ofs, out var ofsv, out _, out _);
        Io.LoadFloats("samples/05_postprocess/data/05_offscreen.verts.lua",
            out var overts, out var ovv, out _, out _);
        Io.LoadText("samples/05_postprocess/data/05_post.vs.slang",
            out var pvs, out var pvsv, out _, out _);
        Io.LoadText("samples/05_postprocess/data/05_post.fs.slang",
            out var pfs, out var pfsv, out _, out _);
        Io.LoadFloats("samples/05_postprocess/data/05_post.verts.lua",
            out var pverts, out var pvv, out _, out _);
        if (ovs == null || ofs == null || overts == null
            || pvs == null || pfs == null || pverts == null) return;

        var rt = Gfx.UseTexture("rt_scene", rtW, rtH, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp, Target = true });
        if (rt == null) return;

        var shOff = Gfx.UseShader("off_shader", ovs, ofs, ovsv * 31 + ofsv);
        var bOff = Gfx.UseBuffer("off_verts", Gfx.BufferType.Vertex, overts, ovv);
        if (shOff == null || bOff == null) return;
        Gfx.BeginPass(new PassOpts
        {
            Target = rt,
            ClearColor = new double[] { 0.1, 0.1, 0.2, 1.0 },
        });
        Gfx.Draw(3,
            new Dictionary<string, object> { ["verts"] = bOff },
            new DrawOpts { Shader = shOff, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();

        var shPost = Gfx.UseShader("post_shader", pvs, pfs, pvsv * 31 + pfsv);
        var bPost = Gfx.UseBuffer("post_verts", Gfx.BufferType.Vertex, pverts, pvv);
        if (shPost == null || bPost == null) return;
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new double[] { 0.0, 0.0, 0.0, 1.0 },
        });
        Gfx.Draw(6,
            new Dictionary<string, object> { ["verts"] = bPost, ["scene"] = rt },
            new DrawOpts { Shader = shPost, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }
}
