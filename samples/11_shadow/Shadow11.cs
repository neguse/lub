// lub の samples/11_shadow (Haxe 版 Shadow11.hx) の TinyC# 版 entry。
// 実行: lub samples/11_shadow/Shadow11.csproj (transpile + watch + hot reload)
// Shadow mapping: render light-space depth into an offscreen target with
// a depth attachment, then use it as a comparison sampler in the scene pass.
using System;
using System.Collections.Generic;

public static class Shadow11
{
    const int SHADOW_SIZE = 1024;

    static float tAccum = 0;

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

    static void AddFloor(List<float> dst)
    {
        var n = new List<float> { 0, 1, 0 };
        Shapes.quad(dst, new List<float> { -2.3f, 0, -1.55f },
            new List<float> { 2.3f, 0, -1.55f },
            new List<float> { 2.3f, 0, 1.75f },
            new List<float> { -2.3f, 0, 1.75f }, n,
            new List<float> { 0.50f, 0.55f, 0.50f, 1.0f });

        var line = new List<float> { 0.38f, 0.42f, 0.39f, 1.0f };
        for (int i = -4; i <= 4; i++)
        {
            float x = i * 0.48f;
            Shapes.quad(dst, new List<float> { x - 0.005f, 0.003f, -1.55f },
                new List<float> { x + 0.005f, 0.003f, -1.55f },
                new List<float> { x + 0.005f, 0.003f, 1.75f },
                new List<float> { x - 0.005f, 0.003f, 1.75f }, n, line);
        }
        for (int i = -3; i <= 3; i++)
        {
            float z = i * 0.48f;
            Shapes.quad(dst, new List<float> { -2.3f, 0.003f, z - 0.005f },
                new List<float> { 2.3f, 0.003f, z - 0.005f },
                new List<float> { 2.3f, 0.003f, z + 0.005f },
                new List<float> { -2.3f, 0.003f, z + 0.005f }, n, line);
        }
    }

    static void AddCasters(List<float> dst, float t)
    {
        Shapes.box(dst, -0.05f, 0.12f, 0.48f, 0.88f, 0.24f, 0.34f,
            new List<float> { 0.95f, 0.76f, 0.38f, 1.0f });
        Shapes.box(dst, -0.58f, 0.52f + (float)Math.Sin(t * 1.4f) * 0.07f, -0.12f,
            0.42f, 0.42f, 0.42f, new List<float> { 0.18f, 0.72f, 0.78f, 1.0f });
        Shapes.sphere(dst, 0.62f + (float)Math.Cos(t * 1.1f) * 0.20f,
            0.58f + (float)Math.Sin(t * 1.7f) * 0.08f,
            -0.18f + (float)Math.Sin(t * 0.8f) * 0.22f, 0.22f,
            new List<float> { 0.95f, 0.28f, 0.34f, 1.0f }, null, null);
        Shapes.box(dst, 0.92f, 0.34f, 0.36f, 0.18f, 0.68f, 0.18f,
            new List<float> { 0.48f, 0.39f, 0.86f, 1.0f });
    }

    static Mat4 CameraMvp(float t)
    {
        var eye = new Vec3(2.0f + (float)Math.Sin(t * 0.25f) * 0.12f, 1.35f, -2.85f);
        var view = Mat4.lookAtLh(eye, new Vec3(0.05f, 0.34f, 0.12f),
            new Vec3(0, 1, 0));
        return Mat4.perspectiveLh(52, 16.0f / 9.0f, 0.1f, 40.0f).mul(view);
    }

    static Mat4 LightMvp()
    {
        var lightPos = new Vec3(-2.0f, 3.3f, -1.5f);
        var view = Mat4.lookAtLh(lightPos, new Vec3(0.08f, 0.24f, 0.08f),
            new Vec3(0, 1, 0));
        return Mat4.orthoLh(3.4f, 3.4f, 0.1f, 7.0f).mul(view);
    }

    public static void onFrame(float dt)
    {
        tAccum = tAccum + dt;

        Io.load_text("samples/11_shadow/data/11_shadow_depth.vs.slang",
            out var dvs, out var dvsv, out _, out _);
        Io.load_text("samples/11_shadow/data/11_shadow_depth.fs.slang",
            out var dfs, out var dfsv, out _, out _);
        Io.load_text("samples/11_shadow/data/11_shadow_scene.vs.slang",
            out var svs, out var svsv, out _, out _);
        Io.load_text("samples/11_shadow/data/11_shadow_scene.fs.slang",
            out var sfs, out var sfsv, out _, out _);
        if (dvs == null || dfs == null || svs == null || sfs == null) return;

        var depthShader = Gfx.use_shader("shadow_depth_shader", dvs, dfs,
            dvsv * 31 + dfsv);
        var sceneShader = Gfx.use_shader("shadow_scene_shader", svs, sfs,
            svsv * 31 + sfsv);

        var shadowMap = Gfx.use_texture("shadow_map", SHADOW_SIZE,
            SHADOW_SIZE, Gfx.RGBA8, null, 1,
            new TextureOpts { target = true, filter = Gfx.NEAREST, wrap = Gfx.CLAMP });
        var shadowDepth = Gfx.use_texture("shadow_depth", SHADOW_SIZE,
            SHADOW_SIZE, Gfx.DEPTH16, null, 1,
            new TextureOpts { target = true, filter = Gfx.NEAREST, wrap = Gfx.CLAMP });

        // Haxe 版 buildMeshes 相当: scene は floor + casters。
        var casters = new List<float>();
        var scene = new List<float>();
        AddFloor(scene);
        AddCasters(casters, tAccum);
        foreach (var f in casters)
        {
            scene.Add(f);
        }
        var casterBuf = Gfx.use_buffer("shadow_casters", Gfx.VERTEX, casters);
        var sceneBuf = Gfx.use_buffer("shadow_scene", Gfx.VERTEX, scene);
        if (depthShader == null || sceneShader == null || shadowMap == null
            || shadowDepth == null || casterBuf == null || sceneBuf == null)
        {
            return;
        }

        var lmvp = LightMvp().m;

        Gfx.begin_pass(new PassOpts
        {
            target = shadowMap,
            depth_target = shadowDepth,
            clear_color = new float[] { 1.0f, 1.0f, 1.0f, 1.0f },
            clear_depth = 1,
        });
        Gfx.draw(casters.Count / Shapes.STRIDE,
            new Dictionary<string, object>
            {
                ["verts"] = casterBuf,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["light_mvp"] = lmvp,
                },
            },
            new DrawOpts
            {
                shader = depthShader,
                depth = true,
                depth_write = true,
                cull = Gfx.NONE,
            });
        Gfx.end_pass();

        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new float[] { 0.09f, 0.12f, 0.15f, 1.0f },
        });
        Gfx.draw(scene.Count / Shapes.STRIDE,
            new Dictionary<string, object>
            {
                ["verts"] = sceneBuf,
                ["shadow_map"] = shadowMap,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["mvp"] = CameraMvp(tAccum).m,
                    ["light_mvp"] = lmvp,
                },
            },
            new DrawOpts
            {
                shader = sceneShader,
                depth = true,
                depth_write = true,
                cull = Gfx.NONE,
            });
        Gfx.end_pass();
    }
}
