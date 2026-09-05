// lub の samples/06_deferred の entry。
// 実行: lub samples/06_deferred/Deferred06.csproj (transpile + watch + hot reload)
// G-buffer pass (MRT: SV_Target0 -> gbuf0, SV_Target1 -> gbuf1) の後、
// view pass で左半分に gbuf0、右半分に gbuf1 を貼って可視化する。
using System;
using System.Collections.Generic;
using static Lub;

public static class Deferred06
{
    const int rtW = 256;
    const int rtH = 256;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        Lub.Config(new ConfigOpts { Backend = backend });
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
        Io.LoadFloats("samples/06_deferred/data/06_gbuffer.verts.lua",
            out var gverts, out var gvv, out _, out _);
        Io.LoadText("samples/06_deferred/data/06_view.vs.slang",
            out var vvs, out var vvsv, out _, out _);
        Io.LoadText("samples/06_deferred/data/06_view.fs.slang",
            out var vfs, out var vfsv, out _, out _);
        Io.LoadFloats("samples/06_deferred/data/06_view.verts.lua",
            out var vverts, out var vvv, out _, out _);
        if (gvs == null || gfs == null || gverts == null
            || vvs == null || vfs == null || vverts == null)
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
        var bG = Gfx.UseBuffer("gbuf_verts", Gfx.BufferType.Vertex, gverts, gvv);
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
        Gfx.Draw(3,
            new Dictionary<string, object> { ["verts"] = bG },
            new DrawOpts { Shader = shG, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();

        // View pass: split-screen visualization. Left half samples gbuf0,
        // right half samples gbuf1. Same shader; only the texture binding and
        // the (scale, offset) transform uniform differ.
        var shV = Gfx.UseShader("view_shader", vvs, vfs, vvsv * 31 + vfsv);
        var bV = Gfx.UseBuffer("view_verts", Gfx.BufferType.Vertex, vverts, vvv);
        if (shV == null || bV == null) return;
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.0f, 0.0f, 0.0f, 1.0f },
        });
        Gfx.Draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = bV,
                ["gbuf"] = gbuf0,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["transform"] = new float[] { 0.5f, 1.0f, -0.5f, 0.0f },
                },
            },
            new DrawOpts { Shader = shV, Depth = false, Cull = Gfx.Cull.None });
        Gfx.Draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = bV,
                ["gbuf"] = gbuf1,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["transform"] = new float[] { 0.5f, 1.0f, 0.5f, 0.0f },
                },
            },
            new DrawOpts { Shader = shV, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }
}
