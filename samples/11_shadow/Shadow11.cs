// lub の samples/11_shadow の entry。
// 実行: lub samples/11_shadow/Shadow11.csproj (transpile + watch + hot reload)
// Shadow mapping: render light-space depth into an offscreen target with
// a depth attachment, then use it as a comparison sampler in the scene pass.
using System;
using System.Collections.Generic;
using static Lub;

public static class Shadow11
{
    const int shadowSize = 1024;

    static float tAccum = 0;

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

    static void AddFloor(List<float> dst)
    {
        var n = new List<float> { 0, 1, 0 };
        Shapes.Quad(dst, new List<float> { -2.3f, 0, -1.55f },
            new List<float> { 2.3f, 0, -1.55f },
            new List<float> { 2.3f, 0, 1.75f },
            new List<float> { -2.3f, 0, 1.75f }, n,
            new List<float> { 0.50f, 0.55f, 0.50f, 1.0f });

        var line = new List<float> { 0.38f, 0.42f, 0.39f, 1.0f };
        for (int i = -4; i <= 4; i++)
        {
            float x = i * 0.48f;
            Shapes.Quad(dst, new List<float> { x - 0.005f, 0.003f, -1.55f },
                new List<float> { x + 0.005f, 0.003f, -1.55f },
                new List<float> { x + 0.005f, 0.003f, 1.75f },
                new List<float> { x - 0.005f, 0.003f, 1.75f }, n, line);
        }
        for (int i = -3; i <= 3; i++)
        {
            float z = i * 0.48f;
            Shapes.Quad(dst, new List<float> { -2.3f, 0.003f, z - 0.005f },
                new List<float> { 2.3f, 0.003f, z - 0.005f },
                new List<float> { 2.3f, 0.003f, z + 0.005f },
                new List<float> { -2.3f, 0.003f, z + 0.005f }, n, line);
        }
    }

    static void AddCasters(List<float> dst, float t)
    {
        Shapes.Box(dst, -0.05f, 0.12f, 0.48f, 0.88f, 0.24f, 0.34f,
            new List<float> { 0.95f, 0.76f, 0.38f, 1.0f });
        Shapes.Box(dst, -0.58f, 0.52f + (float)Math.Sin(t * 1.4f) * 0.07f, -0.12f,
            0.42f, 0.42f, 0.42f, new List<float> { 0.18f, 0.72f, 0.78f, 1.0f });
        Shapes.Sphere(dst, 0.62f + (float)Math.Cos(t * 1.1f) * 0.20f,
            0.58f + (float)Math.Sin(t * 1.7f) * 0.08f,
            -0.18f + (float)Math.Sin(t * 0.8f) * 0.22f, 0.22f,
            new List<float> { 0.95f, 0.28f, 0.34f, 1.0f }, null, null);
        Shapes.Box(dst, 0.92f, 0.34f, 0.36f, 0.18f, 0.68f, 0.18f,
            new List<float> { 0.48f, 0.39f, 0.86f, 1.0f });
    }

    static Mat4 CameraMvp(float t)
    {
        var eye = new Vec3(2.0f + (float)Math.Sin(t * 0.25f) * 0.12f, 1.35f, -2.85f);
        var view = Mat4.LookAtLh(eye, new Vec3(0.05f, 0.34f, 0.12f),
            new Vec3(0, 1, 0));
        return Mat4.PerspectiveLh(52, 16.0f / 9.0f, 0.1f, 40.0f).Mul(view);
    }

    static Mat4 LightMvp()
    {
        var lightPos = new Vec3(-2.0f, 3.3f, -1.5f);
        var view = Mat4.LookAtLh(lightPos, new Vec3(0.08f, 0.24f, 0.08f),
            new Vec3(0, 1, 0));
        return Mat4.OrthoLh(3.4f, 3.4f, 0.1f, 7.0f).Mul(view);
    }

    public static void OnFrame(float dt)
    {
        tAccum = tAccum + dt;

        Io.LoadText("samples/11_shadow/data/11_shadow_depth.vs.slang",
            out var dvs, out var dvsv, out _, out _);
        Io.LoadText("samples/11_shadow/data/11_shadow_depth.fs.slang",
            out var dfs, out var dfsv, out _, out _);
        Io.LoadText("samples/11_shadow/data/11_shadow_scene.vs.slang",
            out var svs, out var svsv, out _, out _);
        Io.LoadText("samples/11_shadow/data/11_shadow_scene.fs.slang",
            out var sfs, out var sfsv, out _, out _);
        if (dvs == null || dfs == null || svs == null || sfs == null) return;

        var depthShader = Gfx.UseShader("shadow_depth_shader", dvs, dfs,
            dvsv * 31 + dfsv);
        var sceneShader = Gfx.UseShader("shadow_scene_shader", svs, sfs,
            svsv * 31 + sfsv);

        var shadowMap = Gfx.UseTexture("shadow_map", shadowSize,
            shadowSize, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp });
        var shadowDepth = Gfx.UseTexture("shadow_depth", shadowSize,
            shadowSize, Gfx.PixelFormat.Depth16, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp });

        // scene は floor + casters。
        var casters = new List<float>();
        var scene = new List<float>();
        AddFloor(scene);
        AddCasters(casters, tAccum);
        foreach (var f in casters)
        {
            scene.Add(f);
        }
        var casterBuf = Gfx.UseBuffer("shadow_casters", Gfx.BufferType.Vertex, casters);
        var sceneBuf = Gfx.UseBuffer("shadow_scene", Gfx.BufferType.Vertex, scene);
        if (depthShader == null || sceneShader == null || shadowMap == null
            || shadowDepth == null || casterBuf == null || sceneBuf == null)
        {
            return;
        }

        var lmvp = LightMvp().M;

        Gfx.BeginPass(new PassOpts
        {
            Target = shadowMap,
            DepthTarget = shadowDepth,
            ClearColor = new float[] { 1.0f, 1.0f, 1.0f, 1.0f },
            ClearDepth = 1,
        });
        Gfx.Draw(casters.Count / Shapes.Stride,
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
                Shader = depthShader,
                Depth = true,
                DepthWrite = true,
                Cull = Gfx.Cull.None,
            });
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.09f, 0.12f, 0.15f, 1.0f },
        });
        Gfx.Draw(scene.Count / Shapes.Stride,
            new Dictionary<string, object>
            {
                ["verts"] = sceneBuf,
                ["shadow_map"] = shadowMap,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["mvp"] = CameraMvp(tAccum).M,
                    ["light_mvp"] = lmvp,
                },
            },
            new DrawOpts
            {
                Shader = sceneShader,
                Depth = true,
                DepthWrite = true,
                Cull = Gfx.Cull.None,
            });
        Gfx.EndPass();
    }
}
