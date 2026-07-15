// lub の samples/11_shadow (Haxe 版 Shadow11.hx) の TinyC# 版 entry。
// 実行: lub samples/11_shadow/Shadow11.csproj (transpile + watch + hot reload)
// Shadow mapping: render light-space depth into an offscreen target with
// a depth attachment, then use it as a comparison sampler in the scene pass.
using System;
using System.Collections.Generic;

public static class Shadow11
{
    const int SHADOW_SIZE = 1024;

    static double tAccum = 0;

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND") ?? "native";
        Lub.config(new ConfigOpts { backend = backend });
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    static void AddFloor(List<double> dst)
    {
        var n = new List<double> { 0, 1, 0 };
        Shapes.quad(dst, new List<double> { -2.3, 0, -1.55 },
            new List<double> { 2.3, 0, -1.55 },
            new List<double> { 2.3, 0, 1.75 },
            new List<double> { -2.3, 0, 1.75 }, n,
            new List<double> { 0.50, 0.55, 0.50, 1.0 });

        var line = new List<double> { 0.38, 0.42, 0.39, 1.0 };
        for (int i = -4; i <= 4; i++)
        {
            double x = i * 0.48;
            Shapes.quad(dst, new List<double> { x - 0.005, 0.003, -1.55 },
                new List<double> { x + 0.005, 0.003, -1.55 },
                new List<double> { x + 0.005, 0.003, 1.75 },
                new List<double> { x - 0.005, 0.003, 1.75 }, n, line);
        }
        for (int i = -3; i <= 3; i++)
        {
            double z = i * 0.48;
            Shapes.quad(dst, new List<double> { -2.3, 0.003, z - 0.005 },
                new List<double> { 2.3, 0.003, z - 0.005 },
                new List<double> { 2.3, 0.003, z + 0.005 },
                new List<double> { -2.3, 0.003, z + 0.005 }, n, line);
        }
    }

    static void AddCasters(List<double> dst, double t)
    {
        Shapes.box(dst, -0.05, 0.12, 0.48, 0.88, 0.24, 0.34,
            new List<double> { 0.95, 0.76, 0.38, 1.0 });
        Shapes.box(dst, -0.58, 0.52 + Math.Sin(t * 1.4) * 0.07, -0.12,
            0.42, 0.42, 0.42, new List<double> { 0.18, 0.72, 0.78, 1.0 });
        Shapes.sphere(dst, 0.62 + Math.Cos(t * 1.1) * 0.20,
            0.58 + Math.Sin(t * 1.7) * 0.08,
            -0.18 + Math.Sin(t * 0.8) * 0.22, 0.22,
            new List<double> { 0.95, 0.28, 0.34, 1.0 }, null, null);
        Shapes.box(dst, 0.92, 0.34, 0.36, 0.18, 0.68, 0.18,
            new List<double> { 0.48, 0.39, 0.86, 1.0 });
    }

    static Mat4 CameraMvp(double t)
    {
        var eye = new Vec3(2.0 + Math.Sin(t * 0.25) * 0.12, 1.35, -2.85);
        var view = Mat4.lookAtLh(eye, new Vec3(0.05, 0.34, 0.12),
            new Vec3(0, 1, 0));
        return Mat4.perspectiveLh(52, 16.0 / 9.0, 0.1, 40.0).mul(view);
    }

    static Mat4 LightMvp()
    {
        var lightPos = new Vec3(-2.0, 3.3, -1.5);
        var view = Mat4.lookAtLh(lightPos, new Vec3(0.08, 0.24, 0.08),
            new Vec3(0, 1, 0));
        return Mat4.orthoLh(3.4, 3.4, 0.1, 7.0).mul(view);
    }

    public static void onFrame(double dt)
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
        var casters = new List<double>();
        var scene = new List<double>();
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
            clear_color = new double[] { 1.0, 1.0, 1.0, 1.0 },
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
            clear_color = new double[] { 0.09, 0.12, 0.15, 1.0 },
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
