// lub の samples/15_render_primitives の entry。
// 実行: lub samples/15_render_primitives/RenderPrimitives15.csproj (transpile + watch + hot reload)
// R16F/RG16F/R32F の色 target、depth target 付きパス、compute の storage 書き込みを
// 5 枚のパネルとして main_tex に並べる。

using System;
using System.Collections.Generic;
using static Lub;

public static class RenderPrimitives15
{
    const int w = 640;
    const int h = 360;
    const int rtw = 160;
    const int rth = 90;

    static BufferRef? quad;
    static BufferRef? tri;
    static float tAccum = 0.0f;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        Lub.Config(new ConfigOpts { Backend = backend, Width = w, Height = h });
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    static TextureRef? Target(string key, Gfx.PixelFormat fmt, Gfx.Filter filter,
        bool storage)
    {
        return Gfx.UseTexture(key, rtw, rth, fmt, null, 1, new TextureOpts
        {
            Target = true,
            Filter = filter,
            Wrap = Gfx.Wrap.Clamp,
            Storage = storage,
        });
    }

    static void EnsureGeometry()
    {
        if (quad == null)
        {
            quad = Gfx.UseBuffer("rp15_quad", Gfx.BufferType.Vertex, new List<float>
            {
                -1.0f, -1.0f, 0.0f, 1.0f,
                1.0f, -1.0f, 1.0f, 1.0f,
                1.0f, 1.0f, 1.0f, 0.0f,
                -1.0f, -1.0f, 0.0f, 1.0f,
                1.0f, 1.0f, 1.0f, 0.0f,
                -1.0f, 1.0f, 0.0f, 0.0f,
            }, 1);
        }
        if (tri == null)
        {
            tri = Gfx.UseBuffer("rp15_tri", Gfx.BufferType.Vertex, new List<float>
            {
                -0.75f, -0.70f, 0.25f,
                0.85f, -0.65f, 0.75f,
                -0.10f, 0.82f, 0.55f,
            }, 1);
        }
    }

    static ShaderRef? Shader2(string key, string vsPath, string fsPath)
    {
        Io.LoadText("samples/15_render_primitives/data/" + vsPath,
            out var vs, out var vsv, out _, out _);
        Io.LoadText("samples/15_render_primitives/data/" + fsPath,
            out var fs, out var fsv, out _, out _);
        if (vs == null || fs == null) return null;
        return Gfx.UseShader(key, vs, fs, vsv * 31 + fsv);
    }

    static ShaderRef? ShaderC(string key, string csPath)
    {
        Io.LoadText("samples/15_render_primitives/data/" + csPath,
            out var cs, out var csv, out _, out _);
        if (cs == null) return null;
        return Gfx.UseShaderCompute(key, cs, csv);
    }

    static void DrawPanel(ShaderRef shader, TextureRef tex, BufferRef verts,
        float x, float y, float sx, float sy, List<float> tint,
        float mode)
    {
        Gfx.Draw(6, new Dictionary<string, object>
        {
            ["verts"] = verts,
            ["scene"] = tex,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["transform"] = new List<float> { sx, sy, x, y },
                ["tint"] = tint,
                ["mode"] = new List<float> { mode, 0.0f, 0.0f, 0.0f },
            },
        }, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
    }

    static void DrawFill(BufferRef verts, ShaderRef fill, float r, float g,
        float b)
    {
        Gfx.Draw(6, new Dictionary<string, object>
        {
            ["verts"] = verts,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["fill"] = new List<float> { r, g, b, 1.0f },
            },
        }, new DrawOpts { Shader = fill, Depth = false, Cull = Gfx.Cull.None });
    }

    public static void OnFrame(float dt)
    {
        EnsureGeometry();
        tAccum = tAccum + dt;

        var fill = Shader2("rp15_fill", "15_quad.vs.slang", "15_fill.fs.slang");
        var depthShader = Shader2("rp15_depth_scene",
            "15_depth_scene.vs.slang", "15_depth_scene.fs.slang");
        var present = Shader2("rp15_present",
            "15_present.vs.slang", "15_present.fs.slang");
        var compute = ShaderC("rp15_compute_tex", "15_storage.cs.slang");
        var quadBuf = quad;
        var triBuf = tri;
        if (fill == null || depthShader == null || present == null
            || compute == null || quadBuf == null || triBuf == null) return;

        var r16 = Target("rp15_r16f", Gfx.PixelFormat.R16f, Gfx.Filter.Nearest, false);
        var rg16 = Target("rp15_rg16f", Gfx.PixelFormat.Rg16f, Gfx.Filter.Nearest, false);
        var r32 = Target("rp15_r32f", Gfx.PixelFormat.R32f, Gfx.Filter.Nearest, false);
        var depthColor = Target("rp15_depth_color", Gfx.PixelFormat.Rgba8, Gfx.Filter.Nearest,
            false);
        var depthTex = Target("rp15_depth", Gfx.PixelFormat.Depth32f, Gfx.Filter.Nearest, false);
        var storageTex = Target("rp15_storage", Gfx.PixelFormat.Rgba32f, Gfx.Filter.Linear, true);
        if (r16 == null || rg16 == null || r32 == null || depthColor == null
            || depthTex == null || storageTex == null) return;

        Gfx.BeginPass(new PassOpts
        {
            Target = r16,
            ClearColor = new float[] { 0.0f, 0.0f, 0.0f, 1.0f },
        });
        DrawFill(quadBuf, fill, 0.25f, 0.0f, 0.0f);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts
        {
            Target = rg16,
            ClearColor = new float[] { 0.0f, 0.0f, 0.0f, 1.0f },
        });
        DrawFill(quadBuf, fill, 0.1f, 0.85f, 0.0f);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts
        {
            Target = r32,
            ClearColor = new float[] { 0.0f, 0.0f, 0.0f, 1.0f },
        });
        DrawFill(quadBuf, fill, 0.85f, 0.0f, 0.0f);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts
        {
            Target = depthColor,
            DepthTarget = depthTex,
            ClearColor = new float[] { 0.02f, 0.02f, 0.04f, 1.0f },
            ClearDepth = 1.0f,
        });
        Gfx.Draw(3, new Dictionary<string, object> { ["verts"] = triBuf },
            new DrawOpts
            {
                Shader = depthShader,
                Depth = true,
                DepthWrite = true,
                Cull = Gfx.Cull.None,
            });
        Gfx.EndPass();

        Gfx.Dispatch((int)Math.Ceiling(rtw / 8.0f),
            (int)Math.Ceiling(rth / 8.0f), 1,
            new Dictionary<string, object>
            {
                ["dst"] = storageTex,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["params"] = new List<float>
                        { rtw, rth, tAccum * 0.6f, 0.0f },
                },
            }, new DispatchOpts { Shader = compute });

        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.025f, 0.03f, 0.04f, 1.0f },
        });
        DrawPanel(present, r16, quadBuf, -0.66f, 0.47f, 0.29f, 0.42f,
            new List<float> { 1.0f, 0.35f, 0.25f, 1.0f }, 0.0f);
        DrawPanel(present, rg16, quadBuf, 0.0f, 0.47f, 0.29f, 0.42f,
            new List<float> { 0.35f, 1.0f, 0.55f, 1.0f }, 0.0f);
        DrawPanel(present, r32, quadBuf, 0.66f, 0.47f, 0.29f, 0.42f,
            new List<float> { 0.55f, 0.72f, 1.0f, 1.0f }, 0.0f);
        DrawPanel(present, depthColor, quadBuf, -0.34f, -0.48f, 0.29f, 0.42f,
            new List<float> { 0.8f, 0.9f, 1.0f, 1.0f }, 0.0f);
        DrawPanel(present, storageTex, quadBuf, 0.34f, -0.48f, 0.29f, 0.42f,
            new List<float> { 1.0f, 1.0f, 1.0f, 1.0f }, 0.0f);
        Gfx.EndPass();
    }
}
