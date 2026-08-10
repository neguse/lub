// lub の samples/15_render_primitives (Haxe 版 RenderPrimitives15.hx) の TinyC# 版 entry。
// 実行: lub samples/15_render_primitives/RenderPrimitives15.csproj (transpile + watch + hot reload)
// R16F/RG16F/R32F の色 target、depth target 付きパス、compute の storage 書き込みを
// 5 枚のパネルとして main_tex に並べる。

using System;
using System.Collections.Generic;

public static class RenderPrimitives15
{
    const int W = 640;
    const int H = 360;
    const int RTW = 160;
    const int RTH = 90;

    static BufferRef? quad;
    static BufferRef? tri;
    static float tAccum = 0.0f;

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND");
        Lub.config(new ConfigOpts { backend = backend, width = W, height = H });
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    static TextureRef? Target(string key, int fmt, int filter, bool storage)
    {
        return Gfx.use_texture(key, RTW, RTH, fmt, null, 1, new TextureOpts
        {
            target = true,
            filter = filter,
            wrap = Gfx.CLAMP,
            storage = storage,
        });
    }

    static void EnsureGeometry()
    {
        if (quad == null)
        {
            quad = Gfx.use_buffer("rp15_quad", Gfx.VERTEX, new List<float>
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
            tri = Gfx.use_buffer("rp15_tri", Gfx.VERTEX, new List<float>
            {
                -0.75f, -0.70f, 0.25f,
                0.85f, -0.65f, 0.75f,
                -0.10f, 0.82f, 0.55f,
            }, 1);
        }
    }

    static ShaderRef? Shader2(string key, string vsPath, string fsPath)
    {
        Io.load_text("samples/15_render_primitives/data/" + vsPath,
            out var vs, out var vsv, out _, out _);
        Io.load_text("samples/15_render_primitives/data/" + fsPath,
            out var fs, out var fsv, out _, out _);
        if (vs == null || fs == null) return null;
        return Gfx.use_shader(key, vs, fs, vsv * 31 + fsv);
    }

    static ShaderRef? ShaderC(string key, string csPath)
    {
        Io.load_text("samples/15_render_primitives/data/" + csPath,
            out var cs, out var csv, out _, out _);
        if (cs == null) return null;
        return Gfx.use_shader_compute(key, cs, csv);
    }

    static void DrawPanel(ShaderRef shader, TextureRef tex, BufferRef verts,
        float x, float y, float sx, float sy, List<float> tint,
        float mode)
    {
        Gfx.draw(6, new Dictionary<string, object>
        {
            ["verts"] = verts,
            ["scene"] = tex,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["transform"] = new List<float> { sx, sy, x, y },
                ["tint"] = tint,
                ["mode"] = new List<float> { mode, 0.0f, 0.0f, 0.0f },
            },
        }, new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
    }

    static void DrawFill(BufferRef verts, ShaderRef fill, float r, float g,
        float b)
    {
        Gfx.draw(6, new Dictionary<string, object>
        {
            ["verts"] = verts,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["fill"] = new List<float> { r, g, b, 1.0f },
            },
        }, new DrawOpts { shader = fill, depth = false, cull = Gfx.NONE });
    }

    public static void onFrame(float dt)
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

        var r16 = Target("rp15_r16f", Gfx.R16F, Gfx.NEAREST, false);
        var rg16 = Target("rp15_rg16f", Gfx.RG16F, Gfx.NEAREST, false);
        var r32 = Target("rp15_r32f", Gfx.R32F, Gfx.NEAREST, false);
        var depthColor = Target("rp15_depth_color", Gfx.RGBA8, Gfx.NEAREST,
            false);
        var depthTex = Target("rp15_depth", Gfx.DEPTH32F, Gfx.NEAREST, false);
        var storageTex = Target("rp15_storage", Gfx.RGBA32F, Gfx.LINEAR, true);
        if (r16 == null || rg16 == null || r32 == null || depthColor == null
            || depthTex == null || storageTex == null) return;

        Gfx.begin_pass(new PassOpts
        {
            target = r16,
            clear_color = new float[] { 0.0f, 0.0f, 0.0f, 1.0f },
        });
        DrawFill(quadBuf, fill, 0.25f, 0.0f, 0.0f);
        Gfx.end_pass();

        Gfx.begin_pass(new PassOpts
        {
            target = rg16,
            clear_color = new float[] { 0.0f, 0.0f, 0.0f, 1.0f },
        });
        DrawFill(quadBuf, fill, 0.1f, 0.85f, 0.0f);
        Gfx.end_pass();

        Gfx.begin_pass(new PassOpts
        {
            target = r32,
            clear_color = new float[] { 0.0f, 0.0f, 0.0f, 1.0f },
        });
        DrawFill(quadBuf, fill, 0.85f, 0.0f, 0.0f);
        Gfx.end_pass();

        Gfx.begin_pass(new PassOpts
        {
            target = depthColor,
            depth_target = depthTex,
            clear_color = new float[] { 0.02f, 0.02f, 0.04f, 1.0f },
            clear_depth = 1.0f,
        });
        Gfx.draw(3, new Dictionary<string, object> { ["verts"] = triBuf },
            new DrawOpts
            {
                shader = depthShader,
                depth = true,
                depth_write = true,
                cull = Gfx.NONE,
            });
        Gfx.end_pass();

        Gfx.dispatch((int)Math.Ceiling(RTW / 8.0f),
            (int)Math.Ceiling(RTH / 8.0f), 1,
            new Dictionary<string, object>
            {
                ["dst"] = storageTex,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["params"] = new List<float>
                        { RTW, RTH, tAccum * 0.6f, 0.0f },
                },
            }, new DispatchOpts { shader = compute });

        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new float[] { 0.025f, 0.03f, 0.04f, 1.0f },
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
        Gfx.end_pass();
    }
}
