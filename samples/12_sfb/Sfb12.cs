// lub の samples/12_sfb (Haxe 版 Sfb12.hx) の TinyC# 版 entry。
// 実行: lub samples/12_sfb/Sfb12.csproj (transpile + watch + hot reload)
//
// 3D-game-shaders-for-beginners style showcase.
//
// A deferred-ish pipeline: one geometry pass fills a G-buffer
//   gColor    (RGBA8)   Blinn-Phong lit color
//   gNormal   (RGBA16F) view-space normal
//   gPosition (RGBA16F) view-space position + linear depth in .w
// then a chain of full-screen post passes reads the G-buffer to add fog,
// outline, SSAO, bloom, DoF, and screen-space toy effects. Each post pass is a
// fullscreen quad draw sampling the previous result (ping-pong between two
// RGBA8 work textures).

using System;
using System.Collections.Generic;
using static Lub;

public static class Sfb12
{
    // render-target size; set from Gfx.size() each frame so the whole post
    // chain runs at the real drawable resolution (smaller = faster on weak
    // devices).
    static int rtW = 1280;
    static int rtH = 720;
    const double waterY = 0.12; // world height of the water plane

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

    // Std.parseFloat 相当 (env 変数の "-1.25" 形式)。
    static double ParseNum(string s)
    {
        return double.Parse(s);
    }

    // ---- geometry (world-space, model baked on CPU) ----

    static void AddFloor(List<double> dst)
    {
        var n = new List<double> { 0, 1, 0 };
        Shapes.Quad(dst, new List<double> { -2.3, 0, -1.55 },
            new List<double> { 2.3, 0, -1.55 },
            new List<double> { 2.3, 0, 1.75 },
            new List<double> { -2.3, 0, 1.75 }, n,
            new List<double> { 0.50, 0.55, 0.50, 1.0 });
        var line = new List<double> { 0.36, 0.40, 0.37, 1.0 };
        for (int i = -4; i < 5; i++)
        {
            var x = i * 0.48;
            Shapes.Quad(dst, new List<double> { x - 0.005, 0.003, -1.55 },
                new List<double> { x + 0.005, 0.003, -1.55 },
                new List<double> { x + 0.005, 0.003, 1.75 },
                new List<double> { x - 0.005, 0.003, 1.75 }, n, line);
        }
        for (int i = -3; i < 4; i++)
        {
            var z = i * 0.48;
            Shapes.Quad(dst, new List<double> { -2.3, 0.003, z - 0.005 },
                new List<double> { 2.3, 0.003, z - 0.005 },
                new List<double> { 2.3, 0.003, z + 0.005 },
                new List<double> { -2.3, 0.003, z + 0.005 }, n, line);
        }
    }

    // ---- textured hero object (material demo) ----
    const int texN = 64;
    static List<int>? albedoPx = null;
    static List<int>? normalPx = null;

    // Procedural albedo (checker) + normal map (grid of bumps), generated once.
    static void GenTextures()
    {
        if (albedoPx != null) return;
        albedoPx = new List<int>();
        normalPx = new List<int>();
        for (int y = 0; y < texN; y++)
        {
            for (int x = 0; x < texN; x++)
            {
                var c = (((x >> 3) + (y >> 3)) & 1) == 0;
                if (c)
                {
                    albedoPx.Add(210);
                    albedoPx.Add(175);
                    albedoPx.Add(95);
                    albedoPx.Add(255);
                }
                else
                {
                    albedoPx.Add(70);
                    albedoPx.Add(120);
                    albedoPx.Add(160);
                    albedoPx.Add(255);
                }
                var nx = Math.Sin((double)x / texN * Math.PI * 8) * 0.6;
                var ny = Math.Sin((double)y / texN * Math.PI * 8) * 0.6;
                var l = Math.Sqrt(nx * nx + ny * ny + 1.0);
                normalPx.Add((int)Math.Floor((nx / l * 0.5 + 0.5) * 255));
                normalPx.Add((int)Math.Floor((ny / l * 0.5 + 0.5) * 255));
                normalPx.Add((int)Math.Floor((1.0 / l * 0.5 + 0.5) * 255));
                normalPx.Add(255);
            }
        }
    }

    static List<int>? flowPx = null;
    static List<int>? waterNrmPx = null;
    static List<int>? lutPx = null;
    const int lutN = 16;

    // Flow map (RG = flow direction) + ripple normal map for the water surface.
    static void GenWaterTextures()
    {
        if (flowPx != null) return;
        flowPx = new List<int>();
        waterNrmPx = new List<int>();
        for (int y = 0; y < texN; y++)
        {
            for (int x = 0; x < texN; x++)
            {
                var fx = 0.7;
                var fy = 0.35 * Math.Sin((double)y / texN * Math.PI * 2);
                var fl = Math.Sqrt(fx * fx + fy * fy);
                flowPx.Add((int)Math.Floor((fx / fl * 0.5 + 0.5) * 255));
                flowPx.Add((int)Math.Floor((fy / fl * 0.5 + 0.5) * 255));
                flowPx.Add(128);
                flowPx.Add(255);
                var nx = Math.Sin((double)x / texN * Math.PI * 12) * 0.4;
                var ny = Math.Sin((double)y / texN * Math.PI * 12 + 1.7) * 0.4;
                var l = Math.Sqrt(nx * nx + ny * ny + 1.0);
                waterNrmPx.Add((int)Math.Floor((nx / l * 0.5 + 0.5) * 255));
                waterNrmPx.Add((int)Math.Floor((ny / l * 0.5 + 0.5) * 255));
                waterNrmPx.Add((int)Math.Floor((1.0 / l * 0.5 + 0.5) * 255));
                waterNrmPx.Add(255);
            }
        }
    }

    // 16^3 color lookup table flattened into a 256x16 texture. This is a real
    // LUT sample in the grade pass, with a deliberately subtle teal/warm grade.
    static void GenLut()
    {
        if (lutPx != null) return;
        lutPx = new List<int>();
        for (int g = 0; g < lutN; g++)
        {
            for (int b = 0; b < lutN; b++)
            {
                for (int r = 0; r < lutN; r++)
                {
                    var rr = (double)r / (lutN - 1);
                    var gg = (double)g / (lutN - 1);
                    var bb = (double)b / (lutN - 1);
                    var lum = rr * 0.2126 + gg * 0.7152 + bb * 0.0722;
                    var shadow = 1.0 - lum;
                    var high = lum;
                    var nr = rr * 1.03 + high * 0.035 - shadow * 0.025;
                    var ng = gg * 1.01 + shadow * 0.025 + high * 0.010;
                    var nb = bb * 0.98 + shadow * 0.060 - high * 0.020;
                    lutPx.Add((int)Math.Floor(MathUtil.Saturate(nr) * 255));
                    lutPx.Add((int)Math.Floor(MathUtil.Saturate(ng) * 255));
                    lutPx.Add((int)Math.Floor(MathUtil.Saturate(nb) * 255));
                    lutPx.Add(255);
                }
            }
        }
    }

    static void PushHero(List<double> dst, double cx, double cy, double cz,
        double r, int seg, int ring, int segs, int rings)
    {
        var u0 = (double)seg / segs * Math.PI * 2;
        var v0 = -Math.PI * 0.5 + (double)ring / rings * Math.PI;
        var cv = Math.Cos(v0);
        var nx = Math.Cos(u0) * cv;
        var ny = Math.Sin(v0);
        var nz = Math.Sin(u0) * cv;
        dst.Add(cx + nx * r);
        dst.Add(cy + ny * r);
        dst.Add(cz + nz * r);
        dst.Add(nx);
        dst.Add(ny);
        dst.Add(nz);
        dst.Add((double)seg / segs * 3.0);
        dst.Add((double)ring / rings * 3.0);
    }

    // UV-sphere (pos.xyz, normal.xyz, uv.xy) for the material shader.
    static List<double> BuildHero()
    {
        var dst = new List<double>();
        var cx = 0.1;
        var cy = 0.5;
        var cz = -0.95;
        var r = 0.45;
        var rings = 24;
        var segs = 48;
        for (int ring = 0; ring < rings; ring++)
        {
            for (int seg = 0; seg < segs; seg++)
            {
                PushHero(dst, cx, cy, cz, r, seg, ring, segs, rings);
                PushHero(dst, cx, cy, cz, r, seg + 1, ring, segs, rings);
                PushHero(dst, cx, cy, cz, r, seg + 1, ring + 1, segs, rings);
                PushHero(dst, cx, cy, cz, r, seg, ring, segs, rings);
                PushHero(dst, cx, cy, cz, r, seg + 1, ring + 1, segs, rings);
                PushHero(dst, cx, cy, cz, r, seg, ring + 1, segs, rings);
            }
        }
        return dst;
    }

    static List<double> BuildScene(double t)
    {
        var dst = new List<double>();
        AddFloor(dst);
        Shapes.Box(dst, -0.05, 0.12, 0.48, 0.88, 0.24, 0.34,
            new List<double> { 0.95, 0.76, 0.38, 1.0 });
        Shapes.Box(dst, -0.58, 0.52 + Math.Sin(t * 1.4) * 0.07, -0.12,
            0.42, 0.42, 0.42, new List<double> { 0.18, 0.72, 0.78, 1.0 });
        Shapes.Sphere(dst, 0.62 + Math.Cos(t * 1.1) * 0.20,
            0.58 + Math.Sin(t * 1.7) * 0.08, -0.18 + Math.Sin(t * 0.8) * 0.22,
            0.22, new List<double> { 0.95, 0.28, 0.34, 1.0 }, 14, 28);
        Shapes.Box(dst, 0.92, 0.34, 0.36, 0.18, 0.68, 0.18,
            new List<double> { 0.48, 0.39, 0.86, 1.0 });
        return dst;
    }

    // ---- matrix math (row-major, matches Slang ROW_MAJOR) ----
    // ---- free-fly camera (WASD move, Q/E down/up, left-drag to look) ----
    // Persists across frames. With no input (headless golden) it stays at the
    // initial pose, so captures remain deterministic.
    // eye は Haxe 版では Vec3 field だが、static 初期化子で他ファイルの class
    // (Vec3) を new すると Lua emit の定義順で nil 参照になるため、スカラーで
    // 保持して使う箇所で Vec3 を組む。
    static double camEyeX = 2.0;
    static double camEyeY = 1.35;
    static double camEyeZ = -2.85;
    static double camYaw = -0.581; // looks toward the scene centre
    static double camPitch = -0.277;
    static Mat4? prevViewProj = null; // last frame's proj*view (motion blur)
    // last frame's camera pose, to detect whether the camera actually moved
    // (motion blur only runs when it did, so a still camera stays a clean
    // no-op).
    static double pcEyeX = 2.0;
    static double pcEyeY = 1.35;
    static double pcEyeZ = -2.85;
    static double pcYaw = -0.581;
    static double pcPitch = -0.277;

    static Vec3 ForwardDir()
    {
        var cp = Math.Cos(camPitch);
        return new Vec3(Math.Sin(camYaw) * cp, Math.Sin(camPitch),
            Math.Cos(camYaw) * cp);
    }

    static Mat4 UpdateCamera(double dt)
    {
        var up = new Vec3(0, 1, 0);

        // LUB_SFB_CAM="yaw,pitch,ex,ey,ez" pins the camera to a fixed pose
        // (testing).
        var camStr = Environment.GetEnvironmentVariable("LUB_SFB_CAM");
        if (camStr != null)
        {
            var p = camStr.Split(",");
            if (p.Length >= 5)
            {
                camYaw = ParseNum(p[0]);
                camPitch = ParseNum(p[1]);
                camEyeX = ParseNum(p[2]);
                camEyeY = ParseNum(p[3]);
                camEyeZ = ParseNum(p[4]);
            }
        }

        // LUB_SFB_SPIN auto-orbits the camera (deterministic) so motion blur is
        // visible in a headless capture; default (unset) keeps the golden still.
        if (Environment.GetEnvironmentVariable("LUB_SFB_SPIN") != null)
        {
            camYaw = camYaw + 1.2 * dt;
        }

        // Mouse look: consume the delta every frame (so it never jumps), apply
        // only while the left button is held.
        Input.MouseDelta(out var mdx, out var mdy);
        if (Input.MouseDown(1))
        {
            camYaw = camYaw + mdx * 0.003;
            camPitch = camPitch - mdy * 0.003;
            if (camPitch > 1.5) camPitch = 1.5;
            if (camPitch < -1.5) camPitch = -1.5;
        }

        var fwd = ForwardDir();
        var right = up.Cross(fwd).Normalize();
        var spd = 2.0 * dt;
        if (Input.KeyDown("w"))
        {
            camEyeX += fwd.X * spd;
            camEyeY += fwd.Y * spd;
            camEyeZ += fwd.Z * spd;
        }
        if (Input.KeyDown("s"))
        {
            camEyeX -= fwd.X * spd;
            camEyeY -= fwd.Y * spd;
            camEyeZ -= fwd.Z * spd;
        }
        if (Input.KeyDown("d"))
        {
            camEyeX += right.X * spd;
            camEyeY += right.Y * spd;
            camEyeZ += right.Z * spd;
        }
        if (Input.KeyDown("a"))
        {
            camEyeX -= right.X * spd;
            camEyeY -= right.Y * spd;
            camEyeZ -= right.Z * spd;
        }
        if (Input.KeyDown("e")) camEyeY += spd;
        if (Input.KeyDown("q")) camEyeY -= spd;

        var eye = new Vec3(camEyeX, camEyeY, camEyeZ);
        var target = new Vec3(camEyeX + fwd.X, camEyeY + fwd.Y,
            camEyeZ + fwd.Z);
        return Mat4.LookAtLh(eye, target, up);
    }

    // ---- fullscreen quad (pos.xy, uv) ----
    // Quad used for the final present (offscreen -> swapchain). The runtime
    // y-flips the swapchain, so this maps clip y = -1 to uv.y = 0.
    static List<double> quadVerts = new List<double>
    {
        -1, -1, 0, 0,
        1, -1, 1, 0,
        1, 1, 1, 1,
        -1, -1, 0, 0,
        1, 1, 1, 1,
        -1, 1, 0, 1,
    };

    // Quad used for offscreen -> offscreen post passes. Offscreen targets are
    // not y-flipped, so we flip uv.y here to keep every intermediate blit an
    // identity (no orientation drift no matter how many passes we chain).
    static List<double> quadVertsFlip = new List<double>
    {
        -1, -1, 0, 1,
        1, -1, 1, 1,
        1, 1, 1, 0,
        -1, -1, 0, 1,
        1, 1, 1, 0,
        -1, 1, 0, 0,
    };

    public static void OnFrame(double dt)
    {
        tAccum = tAccum + dt;

        // size the offscreen chain to the real drawable (canvas/swapchain).
        Gfx.Size(out var szw, out var szh);
        rtW = szw;
        rtH = szh;

        var gShader = Shader2("sfb_gbuf", "12_gbuffer.vs.slang",
            "12_gbuffer.fs.slang");
        var matShader = Shader2("sfb_mat", "12_mat.vs.slang",
            "12_mat.fs.slang");
        var shFlatShader = Shader2("sfb_shflat", "12_shadow_flat.vs.slang",
            "12_shadow.fs.slang");
        var shHeroShader = Shader2("sfb_shhero", "12_shadow_hero.vs.slang",
            "12_shadow.fs.slang");
        var pShader = FsShader("sfb_present", "12_present.fs.slang");
        var ssaoShader = Shader2("sfb_ssao", "12_ssao.vs.slang",
            "12_ssao.fs.slang");
        var fogShader = FsShader("sfb_fog", "12_fog.fs.slang");
        var brightShader = FsShader("sfb_bright", "12_bright.fs.slang");
        var blurHShader = FsShader("sfb_blurh", "12_blur_h.fs.slang");
        var blurVShader = FsShader("sfb_blurv", "12_blur_v.fs.slang");
        var combineShader = FsShader("sfb_combine", "12_combine.fs.slang");
        var outlineShader = FsShader("sfb_outline", "12_outline.fs.slang");
        var dofShader = FsShader("sfb_dof", "12_dof.fs.slang");
        var motionShader = Shader2("sfb_motion", "12_motion.vs.slang",
            "12_motion.fs.slang");
        var waterShader = Shader2("sfb_water", "12_water.vs.slang",
            "12_water.fs.slang");
        var screenShader = Shader2("sfb_screen", "12_screen.vs.slang",
            "12_screen.fs.slang");
        var gradeShader = Shader2("sfb_grade", "12_grade.vs.slang",
            "12_grade.fs.slang");
        if (gShader == null || matShader == null || shFlatShader == null
            || shHeroShader == null || pShader == null || ssaoShader == null
            || fogShader == null || brightShader == null
            || blurHShader == null || blurVShader == null
            || combineShader == null || outlineShader == null
            || dofShader == null || motionShader == null
            || waterShader == null || screenShader == null
            || gradeShader == null)
        {
            return;
        }

        // effect isolation mode (LUB_SFB_MODE): 0=composite, 1=posterize,
        // 2=pixelize, 3=chromatic, 4=sharpen, 5=dilation, 6=normal, 7=depth,
        // 8=shadow map. (LUB_SFB_NOWATER=1 skips the water plane.)
        var modeStr = Environment.GetEnvironmentVariable("LUB_SFB_MODE");
        var mode = modeStr == null ? 0 : (int)ParseNum(modeStr);

        // G-buffer + work targets.
        var gColor = Target("sfb_gColor", rtW, rtH, Gfx.PixelFormat.Rgba8, Gfx.Filter.Linear);
        var gNormal = Target("sfb_gNormal", rtW, rtH, Gfx.PixelFormat.Rgba16f,
            Gfx.Filter.Nearest);
        var gPosition = Target("sfb_gPosition", rtW, rtH, Gfx.PixelFormat.Rgba16f,
            Gfx.Filter.Nearest);
        var gDepth = Target("sfb_gDepth", rtW, rtH, Gfx.PixelFormat.Depth32f,
            Gfx.Filter.Nearest);
        var shadowMap = Target("sfb_shadow", 1024, 1024, Gfx.PixelFormat.Rgba8,
            Gfx.Filter.Nearest);
        var shadowDepth = Target("sfb_shadowD", 1024, 1024, Gfx.PixelFormat.Depth32f,
            Gfx.Filter.Nearest);
        var texA = Target("sfb_texA", rtW, rtH, Gfx.PixelFormat.Rgba8, Gfx.Filter.Linear);
        var texB = Target("sfb_texB", rtW, rtH, Gfx.PixelFormat.Rgba8, Gfx.Filter.Linear);
        var bloomA = Target("sfb_bloomA", rtW, rtH, Gfx.PixelFormat.Rgba8, Gfx.Filter.Linear);
        var bloomB = Target("sfb_bloomB", rtW, rtH, Gfx.PixelFormat.Rgba8, Gfx.Filter.Linear);
        if (gColor == null || gNormal == null || gPosition == null
            || gDepth == null || shadowMap == null || shadowDepth == null
            || texA == null || texB == null || bloomA == null
            || bloomB == null)
        {
            return;
        }

        // Scene + camera.
        var scene = BuildScene(tAccum);
        var sceneBuf = Gfx.UseBuffer("sfb_scene", Gfx.BufferType.Vertex, scene);
        var quadBuf = Gfx.UseBuffer("sfb_quad", Gfx.BufferType.Vertex, quadVerts, 1);
        var quadBufF = Gfx.UseBuffer("sfb_quadF", Gfx.BufferType.Vertex, quadVertsFlip,
            1);

        // Textured hero (material demo): generated albedo + normal map. The
        // hero is a procedural UV sphere by default, or the vendored CC0
        // Avocado glTF when LUB_SFB_GLTF is set (exercises the loader's
        // UV/index path + interleave_pnu).
        GenTextures();
        BufferRef? heroBuf = null;
        BufferRef? heroIdx = null;
        var heroCount = 0;
        var heroModel = Mat4.Identity();
        if (Environment.GetEnvironmentVariable("LUB_SFB_GLTF") != null)
        {
            Io.LoadGltf("samples/12_sfb/data/12_avocado.gltf",
                out var meshObj, out var meshVer, out _, out _);
            if (meshObj != null)
            {
                var mesh = meshObj;
                heroBuf = Gfx.UseBuffer("sfb_hero", Gfx.BufferType.Vertex,
                    Io.InterleavePnu(mesh), meshVer);
                heroIdx = Gfx.UseBufferInts("sfb_heroIdx", Gfx.BufferType.Index, mesh.Indices, meshVer);
                heroCount = mesh.IndexCount;
                heroModel = Mat4.ScaleTrans(8.5, new Vec3(0.1, 0.0, -0.7));
            }
        }
        if (heroBuf == null)
        {
            var heroMesh = BuildHero();
            heroBuf = Gfx.UseBuffer("sfb_hero", Gfx.BufferType.Vertex, heroMesh, 1);
            heroCount = heroMesh.Count / 8;
        }
        var albedoTex = Gfx.UseTexture("sfb_albedo", texN, texN, Gfx.PixelFormat.Rgba8,
            albedoPx, 1, new TextureOpts { Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Repeat });
        var normalTex = Gfx.UseTexture("sfb_normalmap", texN, texN,
            Gfx.PixelFormat.Rgba8, normalPx, 1,
            new TextureOpts { Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Repeat });

        GenWaterTextures();
        var flowTex = Gfx.UseTexture("sfb_flow", texN, texN, Gfx.PixelFormat.Rgba8,
            flowPx, 1, new TextureOpts { Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Repeat });
        var waterNrmTex = Gfx.UseTexture("sfb_waternrm", texN, texN,
            Gfx.PixelFormat.Rgba8, waterNrmPx, 1,
            new TextureOpts { Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Repeat });
        GenLut();
        var lutTex = Gfx.UseTexture("sfb_lut", lutN * lutN, lutN,
            Gfx.PixelFormat.Rgba8, lutPx, 1,
            new TextureOpts { Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp });
        if (sceneBuf == null || quadBuf == null || quadBufF == null
            || heroBuf == null || albedoTex == null || normalTex == null
            || flowTex == null || waterNrmTex == null || lutTex == null)
        {
            return;
        }

        var view = UpdateCamera(dt);
        var proj = Mat4.PerspectiveLh(52, (double)rtW / rtH, 0.1, 40.0);
        // Offscreen targets are stored y-down vs the swapchain (the runtime
        // only y-flips the default framebuffer). Pre-flip clip-space Y so the
        // G-buffer is screen-oriented; cull is NONE so the winding change is
        // harmless.
        proj.M[5] = -proj.M[5];
        // direction toward the light
        var worldLight = new Vec3(-0.48, 1.0, -0.32).Normalize();
        var lightView = view.Mat3MulVec3(worldLight).Normalize();
        // directional shadow: orthographic light camera looking at the scene
        // centre.
        var lightLook = Mat4.LookAtLh(
            new Vec3(0.1 + worldLight.X * 6.0, 0.3 + worldLight.Y * 6.0,
                worldLight.Z * 6.0),
            new Vec3(0.1, 0.3, 0.0), new Vec3(0, 1, 0));
        var lightMvp = Mat4.OrthoLh(5.5, 5.5, 0.1, 12.0).Mul(lightLook);

        // Reprojection for motion blur: maps a current view-space point to
        // last frame's clip space = prevViewProj * inverse(currentView). Still
        // camera => reproj == proj => zero velocity.
        var viewProj = proj.Mul(view);
        var invView = view.RigidInverse(new Vec3(camEyeX, camEyeY, camEyeZ));
        var reproj = (prevViewProj ?? viewProj).Mul(invView);
        prevViewProj = viewProj;

        var camMoved = Math.Abs(camEyeX - pcEyeX)
            + Math.Abs(camEyeY - pcEyeY) + Math.Abs(camEyeZ - pcEyeZ)
            + Math.Abs(camYaw - pcYaw) + Math.Abs(camPitch - pcPitch) > 1e-6;
        pcEyeX = camEyeX;
        pcEyeY = camEyeY;
        pcEyeZ = camEyeZ;
        pcYaw = camYaw;
        pcPitch = camPitch;

        // shadow depth pass (light POV) -> shadowMap, then the G-buffer
        // samples it.
        ShadowPass(shadowMap, shadowDepth, shFlatShader, sceneBuf,
            scene.Count / Shapes.Stride, shHeroShader, heroBuf, heroCount,
            heroIdx, heroModel, lightMvp);

        GeometryPass(gShader, sceneBuf, scene.Count / Shapes.Stride, matShader,
            heroBuf, heroCount, heroIdx, heroModel, albedoTex, normalTex,
            shadowMap, lightMvp, gColor, gNormal, gPosition, gDepth, proj,
            view, lightView);

        // SSAO folds AO into the lit color; fog -> bloom -> outline build the
        // look. (proj[0], proj[5]) are the two projection scalars SSAO needs
        // to project a view-space sample point back to uv.
        SsaoPass(texA, ssaoShader, quadBufF, gColor, gNormal, gPosition,
            proj.M[0], proj.M[5]);
        BlitFog(texB, fogShader, quadBufF, texA, gPosition);
        Blit(bloomA, brightShader, quadBufF, texB);
        Blit(bloomB, blurHShader, quadBufF, bloomA);
        Blit(bloomA, blurVShader, quadBufF, bloomB);
        BlitCombine(texA, combineShader, quadBufF, texB, bloomA);
        // texB = beauty
        BlitOutline(texB, outlineShader, quadBufF, texA, gNormal, gPosition);

        // Water: composite a flow-mapped, refracting, foaming plane at
        // y = WATER_Y over the scene (reflection/refraction/foam/flow).
        // texB -> texA.
        if (Environment.GetEnvironmentVariable("LUB_SFB_NOWATER") != null)
        {
            Blit(texA, pShader, quadBufF, texB);
        }
        else
        {
            WaterPass(texA, waterShader, quadBufF, texB, gPosition, flowTex,
                waterNrmTex, invView, tAccum, waterY, proj.M[0], proj.M[5]);
        }

        // Depth of field: blur a copy of the beauty (texA) through the bloom
        // buffers, then lerp by circle-of-confusion -> texB.
        Blit(bloomB, blurHShader, quadBufF, texA);
        Blit(bloomA, blurVShader, quadBufF, bloomB);
        // texB = beauty
        BlitDof(texB, dofShader, quadBufF, texA, bloomA, gPosition);

        // Camera motion blur, only when the camera actually moved (keeps a
        // still camera a clean, deterministic no-op).
        var beauty = texB;
        if (camMoved)
        {
            MotionPass(texA, motionShader, quadBufF, texB, gPosition, reproj);
            beauty = texA;
        }
        var outBuf = beauty == texB ? texA : texB;

        // Parameterised screen effect runs offscreen; debug modes sample the
        // raw G-buffer.
        var screenSrc = mode == 6 ? gNormal
            : mode == 7 ? gPosition
            : mode == 8 ? shadowMap
            : beauty;
        ScreenPass(outBuf, screenShader, quadBufF, screenSrc, mode);
        var gradeOut = outBuf == texA ? texB : texA;
        GradePass(gradeOut, gradeShader, quadBufF, outBuf, lutTex, tAccum);
        Present(pShader, quadBuf, gradeOut);
    }

    // ---- resource + pass helpers (kept out of onFrame to stay under Lua's
    // 200-locals-per-function limit) ----

    static TextureRef? Target(string key, int w, int h, Gfx.PixelFormat fmt,
        Gfx.Filter filter)
    {
        return Gfx.UseTexture(key, w, h, fmt, null, 1,
            new TextureOpts { Target = true, Filter = filter, Wrap = Gfx.Wrap.Clamp });
    }

    static ShaderRef? FsShader(string key, string fsPath)
    {
        return Shader2(key, "12_quad.vs.slang", fsPath);
    }

    static ShaderRef? Shader2(string key, string vsPath, string fsPath)
    {
        Io.LoadText("samples/12_sfb/data/" + vsPath,
            out var vs, out var vsv, out _, out _);
        Io.LoadText("samples/12_sfb/data/" + fsPath,
            out var fs, out var fsv, out _, out _);
        if (vs == null || fs == null) return null;
        return Gfx.UseShader(key, vs, fs, vsv * 31 + fsv);
    }

    static double[] Black()
    {
        return new double[] { 0.0, 0.0, 0.0, 1.0 };
    }

    // shadow depth pass: render flat + hero from the light's POV into
    // shadowMap.
    static void ShadowPass(TextureRef shadowMap, TextureRef shadowDepth,
        ShaderRef flatShader, BufferRef sceneBuf, int count,
        ShaderRef heroShader, BufferRef heroBuf, int heroCount,
        BufferRef? heroIdx, Mat4 heroModel, Mat4 lightMvp)
    {
        var lmvp = lightMvp.M;
        var mv = heroModel.M;
        Gfx.BeginPass(new PassOpts
        {
            Target = shadowMap,
            DepthTarget = shadowDepth,
            ClearColor = new double[] { 1.0, 1.0, 1.0, 1.0 },
            ClearDepth = 1,
        });
        Gfx.Draw(count,
            new Dictionary<string, object>
            {
                ["verts"] = sceneBuf,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["light_mvp"] = lmvp,
                },
            },
            new DrawOpts
            {
                Shader = flatShader,
                Depth = true,
                DepthWrite = true,
                Cull = Gfx.Cull.None,
            });
        var opts = new DrawOpts
        {
            Shader = heroShader,
            Depth = true,
            DepthWrite = true,
            Cull = Gfx.Cull.None,
        };
        if (heroIdx != null)
        {
            Gfx.Draw(heroCount,
                new Dictionary<string, object>
                {
                    ["verts"] = heroBuf,
                    ["indices"] = heroIdx,
                    ["uniforms"] = new Dictionary<string, object>
                    {
                        ["light_mvp"] = lmvp,
                        ["model"] = mv,
                    },
                }, opts);
        }
        else
        {
            Gfx.Draw(heroCount,
                new Dictionary<string, object>
                {
                    ["verts"] = heroBuf,
                    ["uniforms"] = new Dictionary<string, object>
                    {
                        ["light_mvp"] = lmvp,
                        ["model"] = mv,
                    },
                }, opts);
        }
        Gfx.EndPass();
    }

    static void GeometryPass(ShaderRef shader, BufferRef sceneBuf, int count,
        ShaderRef matShader, BufferRef heroBuf, int heroCount,
        BufferRef? heroIdx, Mat4 heroModel, TextureRef albedoTex,
        TextureRef normalTex, TextureRef shadowMap, Mat4 lightMvp,
        TextureRef gColor, TextureRef gNormal, TextureRef gPosition,
        TextureRef gDepth, Mat4 proj, Mat4 view, Vec3 lightView)
    {
        var lt = new double[] { lightView.X, lightView.Y, lightView.Z, 0.0 };
        var pv = proj.M;
        var vv = view.M;
        var mv = heroModel.M;
        var lmvp = lightMvp.M;
        Gfx.BeginPass(new PassOpts
        {
            Targets = new List<TextureRef> { gColor, gNormal, gPosition },
            DepthTarget = gDepth,
            ClearColors = new List<double[]>
            {
                new double[] { 0.09, 0.12, 0.15, 1.0 },
                new double[] { 0.5, 0.5, 1.0, 0.0 },
                new double[] { 0.0, 0.0, 0.0, 0.0 },
            },
            ClearDepth = 1,
        });
        // flat-shaded scene objects
        Gfx.Draw(count,
            new Dictionary<string, object>
            {
                ["verts"] = sceneBuf,
                ["shadow_map"] = shadowMap,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["proj"] = pv,
                    ["view"] = vv,
                    ["light_mvp"] = lmvp,
                    ["light_dir_view"] = lt,
                },
            },
            new DrawOpts
            {
                Shader = shader,
                Depth = true,
                DepthWrite = true,
                Cull = Gfx.Cull.None,
            });
        // textured + normal-mapped hero (same G-buffer); indexed for glTF
        // meshes.
        var opts = new DrawOpts
        {
            Shader = matShader,
            Depth = true,
            DepthWrite = true,
            Cull = Gfx.Cull.None,
        };
        if (heroIdx != null)
        {
            Gfx.Draw(heroCount,
                new Dictionary<string, object>
                {
                    ["verts"] = heroBuf,
                    ["indices"] = heroIdx,
                    ["albedo"] = albedoTex,
                    ["normalmap"] = normalTex,
                    ["shadow_map"] = shadowMap,
                    ["uniforms"] = new Dictionary<string, object>
                    {
                        ["proj"] = pv,
                        ["view"] = vv,
                        ["model"] = mv,
                        ["light_mvp"] = lmvp,
                        ["light_dir_view"] = lt,
                    },
                }, opts);
        }
        else
        {
            Gfx.Draw(heroCount,
                new Dictionary<string, object>
                {
                    ["verts"] = heroBuf,
                    ["albedo"] = albedoTex,
                    ["normalmap"] = normalTex,
                    ["shadow_map"] = shadowMap,
                    ["uniforms"] = new Dictionary<string, object>
                    {
                        ["proj"] = pv,
                        ["view"] = vv,
                        ["model"] = mv,
                        ["light_mvp"] = lmvp,
                        ["light_dir_view"] = lt,
                    },
                }, opts);
        }
        Gfx.EndPass();
    }

    // single-texture blit, no uniform block (bright / blur / passthrough).
    static void Blit(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
            },
            new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void BlitFog(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef gPosition)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
                ["gpos"] = gPosition,
            },
            new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void BlitCombine(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef baseTex, TextureRef bloom)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = baseTex,
                ["bloom"] = bloom,
            },
            new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void BlitOutline(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef gNormal, TextureRef gPosition)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
                ["gnormal"] = gNormal,
                ["gpos"] = gPosition,
            },
            new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void BlitDof(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef blurred, TextureRef gPosition)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
                ["blurred"] = blurred,
                ["gpos"] = gPosition,
            },
            new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void SsaoPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef gColor, TextureRef gNormal, TextureRef gPosition,
        double p00, double p11)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = gColor,
                ["gnormal"] = gNormal,
                ["gpos"] = gPosition,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["params"] = new double[] { p00, p11, 0.0, 0.0 },
                },
            },
            new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void ScreenPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, int mode)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["params"] = new double[] { mode, 0.004, 0.0, 0.0 },
                },
            },
            new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void GradePass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef lut, double time)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
                ["lut"] = lut,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["params"] = new double[] { time, 0.025, 0.65, 2.2 },
                },
            },
            new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void WaterPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef gPosition, TextureRef flowTex,
        TextureRef wnTex, Mat4 iv, double time, double waterY, double p00,
        double p11)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
                ["gpos"] = gPosition,
                ["flowmap"] = flowTex,
                ["waternormal"] = wnTex,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["ir0"] = new double[] { iv.M[0], iv.M[1], iv.M[2], iv.M[3] },
                    ["ir1"] = new double[] { iv.M[4], iv.M[5], iv.M[6], iv.M[7] },
                    ["ir2"] = new double[] { iv.M[8], iv.M[9], iv.M[10], iv.M[11] },
                    ["params"] = new double[] { time, waterY, p00, p11 },
                },
            },
            new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void MotionPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef gPosition, Mat4 m)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
                ["gpos"] = gPosition,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["r0"] = new double[] { m.M[0], m.M[1], m.M[2], m.M[3] },
                    ["r1"] = new double[] { m.M[4], m.M[5], m.M[6], m.M[7] },
                    ["r2"] = new double[] { m.M[8], m.M[9], m.M[10], m.M[11] },
                    ["r3"] = new double[] { m.M[12], m.M[13], m.M[14], m.M[15] },
                },
            },
            new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void Present(ShaderRef shader, BufferRef quad, TextureRef tex)
    {
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = Black(),
        });
        Gfx.Draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
            },
            new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }
}
