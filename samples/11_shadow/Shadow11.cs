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

    static double tAccum = 0;

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

    static void AddFloor(List<double> dst)
    {
        var n = new List<double> { 0, 1, 0 };
        Shapes.Quad(dst, new List<double> { -2.3, 0, -1.55 },
            new List<double> { 2.3, 0, -1.55 },
            new List<double> { 2.3, 0, 1.75 },
            new List<double> { -2.3, 0, 1.75 }, n,
            new List<double> { 0.50, 0.55, 0.50, 1.0 });

        var line = new List<double> { 0.38, 0.42, 0.39, 1.0 };
        for (int i = -4; i <= 4; i++)
        {
            double x = i * 0.48;
            Shapes.Quad(dst, new List<double> { x - 0.005, 0.003, -1.55 },
                new List<double> { x + 0.005, 0.003, -1.55 },
                new List<double> { x + 0.005, 0.003, 1.75 },
                new List<double> { x - 0.005, 0.003, 1.75 }, n, line);
        }
        for (int i = -3; i <= 3; i++)
        {
            double z = i * 0.48;
            Shapes.Quad(dst, new List<double> { -2.3, 0.003, z - 0.005 },
                new List<double> { 2.3, 0.003, z - 0.005 },
                new List<double> { 2.3, 0.003, z + 0.005 },
                new List<double> { -2.3, 0.003, z + 0.005 }, n, line);
        }
    }

    static void AddCasters(List<double> dst, double t)
    {
        Shapes.Box(dst, -0.05, 0.12, 0.48, 0.88, 0.24, 0.34,
            new List<double> { 0.95, 0.76, 0.38, 1.0 });
        Shapes.Box(dst, -0.58, 0.52 + Math.Sin(t * 1.4) * 0.07, -0.12,
            0.42, 0.42, 0.42, new List<double> { 0.18, 0.72, 0.78, 1.0 });
        Shapes.Sphere(dst, 0.62 + Math.Cos(t * 1.1) * 0.20,
            0.58 + Math.Sin(t * 1.7) * 0.08,
            -0.18 + Math.Sin(t * 0.8) * 0.22, 0.22,
            new List<double> { 0.95, 0.28, 0.34, 1.0 }, null, null);
        Shapes.Box(dst, 0.92, 0.34, 0.36, 0.18, 0.68, 0.18,
            new List<double> { 0.48, 0.39, 0.86, 1.0 });
    }

    static Mat4 CameraMvp(double t)
    {
        var eye = new Vec3(2.0 + Math.Sin(t * 0.25) * 0.12, 1.35, -2.85);
        var view = Mat4.LookAtLh(eye, new Vec3(0.05, 0.34, 0.12),
            new Vec3(0, 1, 0));
        return Mat4.PerspectiveLh(52, 16.0 / 9.0, 0.1, 40.0).Mul(view);
    }

    static Mat4 LightMvp()
    {
        var lightPos = new Vec3(-2.0, 3.3, -1.5);
        var view = Mat4.LookAtLh(lightPos, new Vec3(0.08, 0.24, 0.08),
            new Vec3(0, 1, 0));
        return Mat4.OrthoLh(3.4, 3.4, 0.1, 7.0).Mul(view);
    }

    public static void OnFrame(double dt)
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
        var casters = new List<double>();
        var scene = new List<double>();
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
            ClearColor = new double[] { 1.0, 1.0, 1.0, 1.0 },
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
            ClearColor = new double[] { 0.09, 0.12, 0.15, 1.0 },
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
