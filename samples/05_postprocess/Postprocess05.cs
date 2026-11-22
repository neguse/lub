// lub の samples/05_postprocess の entry。
// 実行: lub samples/05_postprocess/Postprocess05.csproj (transpile + watch + hot reload) オフスクリーン RT に三角形を描き、post シェーダで全画面に貼る 2 パス構成。

using System;
using System.Collections.Generic;
using Lub;

public static class Postprocess05
{
    // pos.x, pos.y, pos.z, pad0, color.r, color.g, color.b, color.a
    static List<float> offscreenVerts = new List<float>
    {
         0.0f,  0.7f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f,
        -0.7f, -0.7f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f,
         0.7f, -0.7f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 1.0f,
    };

    // full screen quad in clip space (pos.xy, uv.xy)
    // Vulkan/SDL_GPU clip-space y is down; uv (0,0) is the top-left texel,
    // matching the offscreen pass that rendered at clip y = -1 onto row 0.
    static List<float> postVerts = new List<float>
    {
        -1.0f, -1.0f, 0.0f, 0.0f,
         1.0f, -1.0f, 1.0f, 0.0f,
         1.0f,  1.0f, 1.0f, 1.0f,
        -1.0f, -1.0f, 0.0f, 0.0f,
         1.0f,  1.0f, 1.0f, 1.0f,
        -1.0f,  1.0f, 0.0f, 1.0f,
    };

    const int rtW = 256;
    const int rtH = 256;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        App.Config(new ConfigOpts { Backend = backend });
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    public static void OnFrame(float dt)
    {
        Io.LoadText("samples/05_postprocess/data/05_offscreen.vs.slang",
            out var ovs, out var ovsv, out _, out _);
        Io.LoadText("samples/05_postprocess/data/05_offscreen.fs.slang",
            out var ofs, out var ofsv, out _, out _);
        Io.LoadText("samples/05_postprocess/data/05_post.vs.slang",
            out var pvs, out var pvsv, out _, out _);
        Io.LoadText("samples/05_postprocess/data/05_post.fs.slang",
            out var pfs, out var pfsv, out _, out _);
        if (ovs == null || ofs == null || pvs == null || pfs == null) return;

        var rt = Gfx.UseTexture("rt_scene", rtW, rtH, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp, Target = true });
        if (rt == null) return;

        var shOff = Gfx.UseShader("off_shader", ovs, ofs, ovsv * 31 + ofsv);
        var bOff = Gfx.UseBuffer("off_verts", Gfx.BufferType.Storage, offscreenVerts);
        if (shOff == null || bOff == null) return;
        Gfx.BeginPass(new PassOpts
        {
            Target = rt,
            ClearColor = new float[] { 0.1f, 0.1f, 0.2f, 1.0f },
        });
        var bindings = new Bindings();
        bindings.Set("verts", bOff);
        Gfx.Draw(3, bindings,
            new DrawOpts { Shader = shOff, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();

        var shPost = Gfx.UseShader("post_shader", pvs, pfs, pvsv * 31 + pfsv);
        var bPost = Gfx.UseBuffer("post_verts", Gfx.BufferType.Storage, postVerts);
        if (shPost == null || bPost == null) return;
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.0f, 0.0f, 0.0f, 1.0f },
        });
        bindings.Clear();
        bindings.Set("verts", bPost);
        bindings.Set("scene", rt);
        Gfx.Draw(6, bindings,
            new DrawOpts { Shader = shPost, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }
}
