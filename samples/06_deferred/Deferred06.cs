// lub の samples/06_deferred の entry。
// 実行: lub samples/06_deferred/Deferred06.csproj (transpile + watch + hot reload) G-buffer pass (MRT: SV_Target0 -> gbuf0, SV_Target1 -> gbuf1) の後、view pass で左半分に gbuf0、右半分に gbuf1 を貼って可視化する。
using System;
using System.Collections.Generic;
using Lub;

public static class Deferred06
{
    // pos.x, pos.y, pos.z, pad0, col0.rgba (primary), col1.rgba (complementary)
    static List<float> gbufferVerts = new List<float>
    {
         0.0f,  0.7f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f, 1.0f, 1.0f,
        -0.7f, -0.7f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f, 1.0f, 0.0f, 1.0f, 1.0f,
         0.7f, -0.7f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 1.0f, 1.0f, 1.0f, 0.0f, 1.0f,
    };

    // fullscreen quad (pos.xy, uv.xy). Sample 6's view VS multiplies pos.xy by
    // a per-draw (scale, offset) uniform so this single vertex buffer renders
    // left- and right-half quads from two draws.
    // uv (0,0) is the top-left texel, matching the offscreen pass that writes
    // at clip y = -1 onto row 0 (Vulkan/SDL_GPU convention, as in samples/05_postprocess).
    static List<float> viewVerts = new List<float>
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
        Io.LoadText("samples/06_deferred/data/06_gbuffer.vs.slang",
            out var gvs, out var gvsv, out _, out _);
        Io.LoadText("samples/06_deferred/data/06_gbuffer.fs.slang",
            out var gfs, out var gfsv, out _, out _);
        Io.LoadText("samples/06_deferred/data/06_view.vs.slang",
            out var vvs, out var vvsv, out _, out _);
        Io.LoadText("samples/06_deferred/data/06_view.fs.slang",
            out var vfs, out var vfsv, out _, out _);
        if (gvs == null || gfs == null || vvs == null || vfs == null)
        {
            return;
        }

        // G-buffer attachments. Two render-target textures of the same size.
        var gbuf0 = Gfx.UseTexture("gbuf0", rtW, rtH, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp, Target = true });
        var gbuf1 = Gfx.UseTexture("gbuf1", rtW, rtH, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp, Target = true });
        if (gbuf0 == null || gbuf1 == null) return;

        // G-buffer pass: MRT write. SV_Target0 -> gbuf0, SV_Target1 -> gbuf1.
        var shG = Gfx.UseShader("gbuf_shader", gvs, gfs, gvsv * 31 + gfsv);
        var bG = Gfx.UseBuffer("gbuf_verts", Gfx.BufferType.Storage, gbufferVerts);
        if (shG == null || bG == null) return;
        Gfx.BeginPass(new PassOpts
        {
            Targets = new List<TextureRef> { gbuf0, gbuf1 },
            ClearColors = new List<float[]>
            {
                new float[] { 0.1f, 0.1f, 0.15f, 1.0f },
                new float[] { 0.15f, 0.1f, 0.1f, 1.0f },
            },
        });
        var bindings = new Bindings();
        bindings.Set("verts", bG);
        Gfx.Draw(3, bindings,
            new DrawOpts { Shader = shG, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();

        // View pass: split-screen visualization. Left half samples gbuf0,
        // right half samples gbuf1. Same shader; only the texture binding and
        // the (scale, offset) transform uniform differ.
        var shV = Gfx.UseShader("view_shader", vvs, vfs, vvsv * 31 + vfsv);
        var bV = Gfx.UseBuffer("view_verts", Gfx.BufferType.Storage, viewVerts);
        if (shV == null || bV == null) return;
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.0f, 0.0f, 0.0f, 1.0f },
        });
        bindings.Clear();
        bindings.Set("verts", bV);
        bindings.Set("gbuf", gbuf0);
        bindings.Set("transform", new Vec4(0.5f, 1.0f, -0.5f, 0.0f));
        Gfx.Draw(6, bindings,
            new DrawOpts { Shader = shV, Depth = false, Cull = Gfx.Cull.None });
        bindings.Clear();
        bindings.Set("verts", bV);
        bindings.Set("gbuf", gbuf1);
        bindings.Set("transform", new Vec4(0.5f, 1.0f, 0.5f, 0.0f));
        Gfx.Draw(6, bindings,
            new DrawOpts { Shader = shV, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }
}
