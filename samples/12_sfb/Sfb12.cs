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
using static @string;

public static class Sfb12
{
    // render-target size; set from Gfx.size() each frame so the whole post
    // chain runs at the real drawable resolution (smaller = faster on weak
    // devices).
    static int RT_W = 1280;
    static int RT_H = 720;
    const float WATER_Y = 0.12f; // world height of the water plane

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

    // Std.parseFloat 相当。tcs には数値 parse API が無いので、env 変数が使う
    // "-1.25" 形式 (符号 + 10 進小数) だけを自前で読む。
    static float ParseNum(string s)
    {
        var n = len(s);
        float sign = 1;
        var i = 1;
        if (n >= 1 && @byte(s, 1) == 45) // '-'
        {
            sign = -1;
            i = 2;
        }
        float v = 0;
        while (i <= n && @byte(s, i) >= 48 && @byte(s, i) <= 57)
        {
            v = v * 10 + (@byte(s, i) - 48);
            i = i + 1;
        }
        if (i <= n && @byte(s, i) == 46) // '.'
        {
            i = i + 1;
            var f = 0.1f;
            while (i <= n && @byte(s, i) >= 48 && @byte(s, i) <= 57)
            {
                v = v + (@byte(s, i) - 48) * f;
                f = f * 0.1f;
                i = i + 1;
            }
        }
        return sign * v;
    }

    // ---- geometry (world-space, model baked on CPU) ----

    static void AddFloor(List<float> dst)
    {
        var n = new List<float> { 0, 1, 0 };
        Shapes.quad(dst, new List<float> { -2.3f, 0, -1.55f },
            new List<float> { 2.3f, 0, -1.55f },
            new List<float> { 2.3f, 0, 1.75f },
            new List<float> { -2.3f, 0, 1.75f }, n,
            new List<float> { 0.50f, 0.55f, 0.50f, 1.0f });
        var line = new List<float> { 0.36f, 0.40f, 0.37f, 1.0f };
        for (int i = -4; i < 5; i++)
        {
            var x = i * 0.48f;
            Shapes.quad(dst, new List<float> { x - 0.005f, 0.003f, -1.55f },
                new List<float> { x + 0.005f, 0.003f, -1.55f },
                new List<float> { x + 0.005f, 0.003f, 1.75f },
                new List<float> { x - 0.005f, 0.003f, 1.75f }, n, line);
        }
        for (int i = -3; i < 4; i++)
        {
            var z = i * 0.48f;
            Shapes.quad(dst, new List<float> { -2.3f, 0.003f, z - 0.005f },
                new List<float> { 2.3f, 0.003f, z - 0.005f },
                new List<float> { 2.3f, 0.003f, z + 0.005f },
                new List<float> { -2.3f, 0.003f, z + 0.005f }, n, line);
        }
    }

    // ---- textured hero object (material demo) ----
    const int TEX_N = 64;
    static List<int>? albedoPx = null;
    static List<int>? normalPx = null;

    // Procedural albedo (checker) + normal map (grid of bumps), generated once.
    static void GenTextures()
    {
        if (albedoPx != null) return;
        albedoPx = new List<int>();
        normalPx = new List<int>();
        for (int y = 0; y < TEX_N; y++)
        {
            for (int x = 0; x < TEX_N; x++)
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
                var nx = (float)Math.Sin((float)x / TEX_N * (float)Math.PI * 8) * 0.6f;
                var ny = (float)Math.Sin((float)y / TEX_N * (float)Math.PI * 8) * 0.6f;
                var l = (float)Math.Sqrt(nx * nx + ny * ny + 1.0f);
                normalPx.Add((int)Math.Floor((nx / l * 0.5f + 0.5f) * 255));
                normalPx.Add((int)Math.Floor((ny / l * 0.5f + 0.5f) * 255));
                normalPx.Add((int)Math.Floor((1.0f / l * 0.5f + 0.5f) * 255));
                normalPx.Add(255);
            }
        }
    }

    static List<int>? flowPx = null;
    static List<int>? waterNrmPx = null;
    static List<int>? lutPx = null;
    const int LUT_N = 16;

    // Flow map (RG = flow direction) + ripple normal map for the water surface.
    static void GenWaterTextures()
    {
        if (flowPx != null) return;
        flowPx = new List<int>();
        waterNrmPx = new List<int>();
        for (int y = 0; y < TEX_N; y++)
        {
            for (int x = 0; x < TEX_N; x++)
            {
                var fx = 0.7f;
                var fy = 0.35f * (float)Math.Sin((float)y / TEX_N * (float)Math.PI * 2);
                var fl = (float)Math.Sqrt(fx * fx + fy * fy);
                flowPx.Add((int)Math.Floor((fx / fl * 0.5f + 0.5f) * 255));
                flowPx.Add((int)Math.Floor((fy / fl * 0.5f + 0.5f) * 255));
                flowPx.Add(128);
                flowPx.Add(255);
                var nx = (float)Math.Sin((float)x / TEX_N * (float)Math.PI * 12) * 0.4f;
                var ny = (float)Math.Sin((float)y / TEX_N * (float)Math.PI * 12 + 1.7f) * 0.4f;
                var l = (float)Math.Sqrt(nx * nx + ny * ny + 1.0f);
                waterNrmPx.Add((int)Math.Floor((nx / l * 0.5f + 0.5f) * 255));
                waterNrmPx.Add((int)Math.Floor((ny / l * 0.5f + 0.5f) * 255));
                waterNrmPx.Add((int)Math.Floor((1.0f / l * 0.5f + 0.5f) * 255));
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
        for (int g = 0; g < LUT_N; g++)
        {
            for (int b = 0; b < LUT_N; b++)
            {
                for (int r = 0; r < LUT_N; r++)
                {
                    var rr = (float)r / (LUT_N - 1);
                    var gg = (float)g / (LUT_N - 1);
                    var bb = (float)b / (LUT_N - 1);
                    var lum = rr * 0.2126f + gg * 0.7152f + bb * 0.0722f;
                    var shadow = 1.0f - lum;
                    var high = lum;
                    var nr = rr * 1.03f + high * 0.035f - shadow * 0.025f;
                    var ng = gg * 1.01f + shadow * 0.025f + high * 0.010f;
                    var nb = bb * 0.98f + shadow * 0.060f - high * 0.020f;
                    lutPx.Add((int)Math.Floor(MathUtil.saturate(nr) * 255));
                    lutPx.Add((int)Math.Floor(MathUtil.saturate(ng) * 255));
                    lutPx.Add((int)Math.Floor(MathUtil.saturate(nb) * 255));
                    lutPx.Add(255);
                }
            }
        }
    }

    static void PushHero(List<float> dst, float cx, float cy, float cz,
        float r, int seg, int ring, int segs, int rings)
    {
        var u0 = (float)seg / segs * (float)Math.PI * 2;
        var v0 = -(float)Math.PI * 0.5f + (float)ring / rings * (float)Math.PI;
        var cv = (float)Math.Cos(v0);
        var nx = (float)Math.Cos(u0) * cv;
        var ny = (float)Math.Sin(v0);
        var nz = (float)Math.Sin(u0) * cv;
        dst.Add(cx + nx * r);
        dst.Add(cy + ny * r);
        dst.Add(cz + nz * r);
        dst.Add(nx);
        dst.Add(ny);
        dst.Add(nz);
        dst.Add((float)seg / segs * 3.0f);
        dst.Add((float)ring / rings * 3.0f);
    }

    // UV-sphere (pos.xyz, normal.xyz, uv.xy) for the material shader.
    static List<float> BuildHero()
    {
        var dst = new List<float>();
        var cx = 0.1f;
        var cy = 0.5f;
        var cz = -0.95f;
        var r = 0.45f;
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

    static List<float> BuildScene(float t)
    {
        var dst = new List<float>();
        AddFloor(dst);
        Shapes.box(dst, -0.05f, 0.12f, 0.48f, 0.88f, 0.24f, 0.34f,
            new List<float> { 0.95f, 0.76f, 0.38f, 1.0f });
        Shapes.box(dst, -0.58f, 0.52f + (float)Math.Sin(t * 1.4f) * 0.07f, -0.12f,
            0.42f, 0.42f, 0.42f, new List<float> { 0.18f, 0.72f, 0.78f, 1.0f });
        Shapes.sphere(dst, 0.62f + (float)Math.Cos(t * 1.1f) * 0.20f,
            0.58f + (float)Math.Sin(t * 1.7f) * 0.08f, -0.18f + (float)Math.Sin(t * 0.8f) * 0.22f,
            0.22f, new List<float> { 0.95f, 0.28f, 0.34f, 1.0f }, 14, 28);
        Shapes.box(dst, 0.92f, 0.34f, 0.36f, 0.18f, 0.68f, 0.18f,
            new List<float> { 0.48f, 0.39f, 0.86f, 1.0f });
        return dst;
    }

    // ---- matrix math (row-major, matches Slang ROW_MAJOR) ----
    // ---- free-fly camera (WASD move, Q/E down/up, left-drag to look) ----
    // Persists across frames. With no input (headless golden) it stays at the
    // initial pose, so captures remain deterministic.
    // eye は Haxe 版では Vec3 field だが、static 初期化子で他ファイルの class
    // (Vec3) を new すると Lua emit の定義順で nil 参照になるため、スカラーで
    // 保持して使う箇所で Vec3 を組む。
    static float camEyeX = 2.0f;
    static float camEyeY = 1.35f;
    static float camEyeZ = -2.85f;
    static float camYaw = -0.581f; // looks toward the scene centre
    static float camPitch = -0.277f;
    static Mat4? prevViewProj = null; // last frame's proj*view (motion blur)
    // last frame's camera pose, to detect whether the camera actually moved
    // (motion blur only runs when it did, so a still camera stays a clean
    // no-op).
    static float pcEyeX = 2.0f;
    static float pcEyeY = 1.35f;
    static float pcEyeZ = -2.85f;
    static float pcYaw = -0.581f;
    static float pcPitch = -0.277f;

    static Vec3 ForwardDir()
    {
        var cp = (float)Math.Cos(camPitch);
        return new Vec3((float)Math.Sin(camYaw) * cp, (float)Math.Sin(camPitch),
            (float)Math.Cos(camYaw) * cp);
    }

    static Mat4 UpdateCamera(float dt)
    {
        var up = new Vec3(0, 1, 0);

        // LUB_SFB_CAM="yaw,pitch,ex,ey,ez" pins the camera to a fixed pose
        // (testing).
        var camStr = os.getenv("LUB_SFB_CAM");
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
        if (os.getenv("LUB_SFB_SPIN") != null)
        {
            camYaw = camYaw + 1.2f * dt;
        }

        // Mouse look: consume the delta every frame (so it never jumps), apply
        // only while the left button is held.
        Input.mouse_delta(out var mdx, out var mdy);
        if (Input.mouse_down(1))
        {
            camYaw = camYaw + mdx * 0.003f;
            camPitch = camPitch - mdy * 0.003f;
            if (camPitch > 1.5f) camPitch = 1.5f;
            if (camPitch < -1.5f) camPitch = -1.5f;
        }

        var fwd = ForwardDir();
        var right = up.cross(fwd).normalize();
        var spd = 2.0f * dt;
        if (Input.key_down("w"))
        {
            camEyeX += fwd.x * spd;
            camEyeY += fwd.y * spd;
            camEyeZ += fwd.z * spd;
        }
        if (Input.key_down("s"))
        {
            camEyeX -= fwd.x * spd;
            camEyeY -= fwd.y * spd;
            camEyeZ -= fwd.z * spd;
        }
        if (Input.key_down("d"))
        {
            camEyeX += right.x * spd;
            camEyeY += right.y * spd;
            camEyeZ += right.z * spd;
        }
        if (Input.key_down("a"))
        {
            camEyeX -= right.x * spd;
            camEyeY -= right.y * spd;
            camEyeZ -= right.z * spd;
        }
        if (Input.key_down("e")) camEyeY += spd;
        if (Input.key_down("q")) camEyeY -= spd;

        var eye = new Vec3(camEyeX, camEyeY, camEyeZ);
        var target = new Vec3(camEyeX + fwd.x, camEyeY + fwd.y,
            camEyeZ + fwd.z);
        return Mat4.lookAtLh(eye, target, up);
    }

    // ---- fullscreen quad (pos.xy, uv) ----
    // Quad used for the final present (offscreen -> swapchain). The runtime
    // y-flips the swapchain, so this maps clip y = -1 to uv.y = 0.
    static List<float> quadVerts = new List<float>
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
    static List<float> quadVertsFlip = new List<float>
    {
        -1, -1, 0, 1,
        1, -1, 1, 1,
        1, 1, 1, 0,
        -1, -1, 0, 1,
        1, 1, 1, 0,
        -1, 1, 0, 0,
    };

    public static void onFrame(float dt)
    {
        tAccum = tAccum + dt;

        // size the offscreen chain to the real drawable (canvas/swapchain).
        Gfx.size(out var szw, out var szh);
        RT_W = szw;
        RT_H = szh;

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
        var modeStr = os.getenv("LUB_SFB_MODE");
        var mode = modeStr == null ? 0 : (int)ParseNum(modeStr);

        // G-buffer + work targets.
        var gColor = Target("sfb_gColor", RT_W, RT_H, Gfx.RGBA8, Gfx.LINEAR);
        var gNormal = Target("sfb_gNormal", RT_W, RT_H, Gfx.RGBA16F,
            Gfx.NEAREST);
        var gPosition = Target("sfb_gPosition", RT_W, RT_H, Gfx.RGBA16F,
            Gfx.NEAREST);
        var gDepth = Target("sfb_gDepth", RT_W, RT_H, Gfx.DEPTH32F,
            Gfx.NEAREST);
        var shadowMap = Target("sfb_shadow", 1024, 1024, Gfx.RGBA8,
            Gfx.NEAREST);
        var shadowDepth = Target("sfb_shadowD", 1024, 1024, Gfx.DEPTH32F,
            Gfx.NEAREST);
        var texA = Target("sfb_texA", RT_W, RT_H, Gfx.RGBA8, Gfx.LINEAR);
        var texB = Target("sfb_texB", RT_W, RT_H, Gfx.RGBA8, Gfx.LINEAR);
        var bloomA = Target("sfb_bloomA", RT_W, RT_H, Gfx.RGBA8, Gfx.LINEAR);
        var bloomB = Target("sfb_bloomB", RT_W, RT_H, Gfx.RGBA8, Gfx.LINEAR);
        if (gColor == null || gNormal == null || gPosition == null
            || gDepth == null || shadowMap == null || shadowDepth == null
            || texA == null || texB == null || bloomA == null
            || bloomB == null)
        {
            return;
        }

        // Scene + camera.
        var scene = BuildScene(tAccum);
        var sceneBuf = Gfx.use_buffer("sfb_scene", Gfx.VERTEX, scene);
        var quadBuf = Gfx.use_buffer("sfb_quad", Gfx.VERTEX, quadVerts, 1);
        var quadBufF = Gfx.use_buffer("sfb_quadF", Gfx.VERTEX, quadVertsFlip,
            1);

        // Textured hero (material demo): generated albedo + normal map. The
        // hero is a procedural UV sphere by default, or the vendored CC0
        // Avocado glTF when LUB_SFB_GLTF is set (exercises the loader's
        // UV/index path + interleave_pnu).
        GenTextures();
        BufferRef? heroBuf = null;
        BufferRef? heroIdx = null;
        var heroCount = 0;
        var heroModel = Mat4.identity();
        if (os.getenv("LUB_SFB_GLTF") != null)
        {
            Io.load_gltf("samples/12_sfb/data/12_avocado.gltf",
                out var meshObj, out var meshVer, out _, out _);
            if (meshObj != null)
            {
                var mesh = (MeshData)meshObj;
                heroBuf = Gfx.use_buffer("sfb_hero", Gfx.VERTEX,
                    Io.interleave_pnu(mesh), meshVer);
                // MeshData.indices は List<int> だが use_buffer は
                // List<float> を取る。tcs は型消去なので Lua では同じ table が
                // そのまま渡る (cast は無変換)。
                heroIdx = Gfx.use_buffer("sfb_heroIdx", Gfx.INDEX,
                    (List<float>)(object)mesh.indices, meshVer);
                heroCount = mesh.index_count;
                heroModel = Mat4.scaleTrans(8.5f, new Vec3(0.1f, 0.0f, -0.7f));
            }
        }
        if (heroBuf == null)
        {
            var heroMesh = BuildHero();
            heroBuf = Gfx.use_buffer("sfb_hero", Gfx.VERTEX, heroMesh, 1);
            heroCount = heroMesh.Count / 8;
        }
        var albedoTex = Gfx.use_texture("sfb_albedo", TEX_N, TEX_N, Gfx.RGBA8,
            albedoPx, 1, new TextureOpts { filter = Gfx.LINEAR, wrap = Gfx.REPEAT });
        var normalTex = Gfx.use_texture("sfb_normalmap", TEX_N, TEX_N,
            Gfx.RGBA8, normalPx, 1,
            new TextureOpts { filter = Gfx.LINEAR, wrap = Gfx.REPEAT });

        GenWaterTextures();
        var flowTex = Gfx.use_texture("sfb_flow", TEX_N, TEX_N, Gfx.RGBA8,
            flowPx, 1, new TextureOpts { filter = Gfx.LINEAR, wrap = Gfx.REPEAT });
        var waterNrmTex = Gfx.use_texture("sfb_waternrm", TEX_N, TEX_N,
            Gfx.RGBA8, waterNrmPx, 1,
            new TextureOpts { filter = Gfx.LINEAR, wrap = Gfx.REPEAT });
        GenLut();
        var lutTex = Gfx.use_texture("sfb_lut", LUT_N * LUT_N, LUT_N,
            Gfx.RGBA8, lutPx, 1,
            new TextureOpts { filter = Gfx.LINEAR, wrap = Gfx.CLAMP });
        if (sceneBuf == null || quadBuf == null || quadBufF == null
            || heroBuf == null || albedoTex == null || normalTex == null
            || flowTex == null || waterNrmTex == null || lutTex == null)
        {
            return;
        }

        var view = UpdateCamera(dt);
        var proj = Mat4.perspectiveLh(52, (float)RT_W / RT_H, 0.1f, 40.0f);
        // Offscreen targets are stored y-down vs the swapchain (the runtime
        // only y-flips the default framebuffer). Pre-flip clip-space Y so the
        // G-buffer is screen-oriented; cull is NONE so the winding change is
        // harmless.
        proj.m[5] = -proj.m[5];
        // direction toward the light
        var worldLight = new Vec3(-0.48f, 1.0f, -0.32f).normalize();
        var lightView = view.mat3MulVec3(worldLight).normalize();
        // directional shadow: orthographic light camera looking at the scene
        // centre.
        var lightLook = Mat4.lookAtLh(
            new Vec3(0.1f + worldLight.x * 6.0f, 0.3f + worldLight.y * 6.0f,
                worldLight.z * 6.0f),
            new Vec3(0.1f, 0.3f, 0.0f), new Vec3(0, 1, 0));
        var lightMvp = Mat4.orthoLh(5.5f, 5.5f, 0.1f, 12.0f).mul(lightLook);

        // Reprojection for motion blur: maps a current view-space point to
        // last frame's clip space = prevViewProj * inverse(currentView). Still
        // camera => reproj == proj => zero velocity.
        var viewProj = proj.mul(view);
        var invView = view.rigidInverse(new Vec3(camEyeX, camEyeY, camEyeZ));
        var reproj = (prevViewProj ?? viewProj).mul(invView);
        prevViewProj = viewProj;

        var camMoved = Math.Abs(camEyeX - pcEyeX)
            + Math.Abs(camEyeY - pcEyeY) + Math.Abs(camEyeZ - pcEyeZ)
            + Math.Abs(camYaw - pcYaw) + Math.Abs(camPitch - pcPitch) > 1e-6f;
        pcEyeX = camEyeX;
        pcEyeY = camEyeY;
        pcEyeZ = camEyeZ;
        pcYaw = camYaw;
        pcPitch = camPitch;

        // shadow depth pass (light POV) -> shadowMap, then the G-buffer
        // samples it.
        ShadowPass(shadowMap, shadowDepth, shFlatShader, sceneBuf,
            scene.Count / Shapes.STRIDE, shHeroShader, heroBuf, heroCount,
            heroIdx, heroModel, lightMvp);

        GeometryPass(gShader, sceneBuf, scene.Count / Shapes.STRIDE, matShader,
            heroBuf, heroCount, heroIdx, heroModel, albedoTex, normalTex,
            shadowMap, lightMvp, gColor, gNormal, gPosition, gDepth, proj,
            view, lightView);

        // SSAO folds AO into the lit color; fog -> bloom -> outline build the
        // look. (proj[0], proj[5]) are the two projection scalars SSAO needs
        // to project a view-space sample point back to uv.
        SsaoPass(texA, ssaoShader, quadBufF, gColor, gNormal, gPosition,
            proj.m[0], proj.m[5]);
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
        if (os.getenv("LUB_SFB_NOWATER") != null)
        {
            Blit(texA, pShader, quadBufF, texB);
        }
        else
        {
            WaterPass(texA, waterShader, quadBufF, texB, gPosition, flowTex,
                waterNrmTex, invView, tAccum, WATER_Y, proj.m[0], proj.m[5]);
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

    static TextureRef? Target(string key, int w, int h, int fmt, int filter)
    {
        return Gfx.use_texture(key, w, h, fmt, null, 1,
            new TextureOpts { target = true, filter = filter, wrap = Gfx.CLAMP });
    }

    static ShaderRef? FsShader(string key, string fsPath)
    {
        return Shader2(key, "12_quad.vs.slang", fsPath);
    }

    static ShaderRef? Shader2(string key, string vsPath, string fsPath)
    {
        Io.load_text("samples/12_sfb/data/" + vsPath,
            out var vs, out var vsv, out _, out _);
        Io.load_text("samples/12_sfb/data/" + fsPath,
            out var fs, out var fsv, out _, out _);
        if (vs == null || fs == null) return null;
        return Gfx.use_shader(key, vs, fs, vsv * 31 + fsv);
    }

    static float[] Black()
    {
        return new float[] { 0.0f, 0.0f, 0.0f, 1.0f };
    }

    // shadow depth pass: render flat + hero from the light's POV into
    // shadowMap.
    static void ShadowPass(TextureRef shadowMap, TextureRef shadowDepth,
        ShaderRef flatShader, BufferRef sceneBuf, int count,
        ShaderRef heroShader, BufferRef heroBuf, int heroCount,
        BufferRef? heroIdx, Mat4 heroModel, Mat4 lightMvp)
    {
        var lmvp = lightMvp.m;
        var mv = heroModel.m;
        Gfx.begin_pass(new PassOpts
        {
            target = shadowMap,
            depth_target = shadowDepth,
            clear_color = new float[] { 1.0f, 1.0f, 1.0f, 1.0f },
            clear_depth = 1,
        });
        Gfx.draw(count,
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
                shader = flatShader,
                depth = true,
                depth_write = true,
                cull = Gfx.NONE,
            });
        var opts = new DrawOpts
        {
            shader = heroShader,
            depth = true,
            depth_write = true,
            cull = Gfx.NONE,
        };
        if (heroIdx != null)
        {
            Gfx.draw(heroCount,
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
            Gfx.draw(heroCount,
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
        Gfx.end_pass();
    }

    static void GeometryPass(ShaderRef shader, BufferRef sceneBuf, int count,
        ShaderRef matShader, BufferRef heroBuf, int heroCount,
        BufferRef? heroIdx, Mat4 heroModel, TextureRef albedoTex,
        TextureRef normalTex, TextureRef shadowMap, Mat4 lightMvp,
        TextureRef gColor, TextureRef gNormal, TextureRef gPosition,
        TextureRef gDepth, Mat4 proj, Mat4 view, Vec3 lightView)
    {
        var lt = new float[] { lightView.x, lightView.y, lightView.z, 0.0f };
        var pv = proj.m;
        var vv = view.m;
        var mv = heroModel.m;
        var lmvp = lightMvp.m;
        Gfx.begin_pass(new PassOpts
        {
            targets = new List<TextureRef> { gColor, gNormal, gPosition },
            depth_target = gDepth,
            clear_colors = new List<float[]>
            {
                new float[] { 0.09f, 0.12f, 0.15f, 1.0f },
                new float[] { 0.5f, 0.5f, 1.0f, 0.0f },
                new float[] { 0.0f, 0.0f, 0.0f, 0.0f },
            },
            clear_depth = 1,
        });
        // flat-shaded scene objects
        Gfx.draw(count,
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
                shader = shader,
                depth = true,
                depth_write = true,
                cull = Gfx.NONE,
            });
        // textured + normal-mapped hero (same G-buffer); indexed for glTF
        // meshes.
        var opts = new DrawOpts
        {
            shader = matShader,
            depth = true,
            depth_write = true,
            cull = Gfx.NONE,
        };
        if (heroIdx != null)
        {
            Gfx.draw(heroCount,
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
            Gfx.draw(heroCount,
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
        Gfx.end_pass();
    }

    // single-texture blit, no uniform block (bright / blur / passthrough).
    static void Blit(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
            },
            new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void BlitFog(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef gPosition)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
                ["gpos"] = gPosition,
            },
            new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void BlitCombine(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef baseTex, TextureRef bloom)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = baseTex,
                ["bloom"] = bloom,
            },
            new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void BlitOutline(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef gNormal, TextureRef gPosition)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
                ["gnormal"] = gNormal,
                ["gpos"] = gPosition,
            },
            new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void BlitDof(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef blurred, TextureRef gPosition)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
                ["blurred"] = blurred,
                ["gpos"] = gPosition,
            },
            new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void SsaoPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef gColor, TextureRef gNormal, TextureRef gPosition,
        float p00, float p11)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = gColor,
                ["gnormal"] = gNormal,
                ["gpos"] = gPosition,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["params"] = new float[] { p00, p11, 0.0f, 0.0f },
                },
            },
            new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void ScreenPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, int mode)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["params"] = new float[] { mode, 0.004f, 0.0f, 0.0f },
                },
            },
            new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void GradePass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef lut, float time)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
                ["lut"] = lut,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["params"] = new float[] { time, 0.025f, 0.65f, 2.2f },
                },
            },
            new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void WaterPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef gPosition, TextureRef flowTex,
        TextureRef wnTex, Mat4 iv, float time, float waterY, float p00,
        float p11)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
                ["gpos"] = gPosition,
                ["flowmap"] = flowTex,
                ["waternormal"] = wnTex,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["ir0"] = new float[] { iv.m[0], iv.m[1], iv.m[2], iv.m[3] },
                    ["ir1"] = new float[] { iv.m[4], iv.m[5], iv.m[6], iv.m[7] },
                    ["ir2"] = new float[] { iv.m[8], iv.m[9], iv.m[10], iv.m[11] },
                    ["params"] = new float[] { time, waterY, p00, p11 },
                },
            },
            new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void MotionPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef gPosition, Mat4 m)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
                ["gpos"] = gPosition,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["r0"] = new float[] { m.m[0], m.m[1], m.m[2], m.m[3] },
                    ["r1"] = new float[] { m.m[4], m.m[5], m.m[6], m.m[7] },
                    ["r2"] = new float[] { m.m[8], m.m[9], m.m[10], m.m[11] },
                    ["r3"] = new float[] { m.m[12], m.m[13], m.m[14], m.m[15] },
                },
            },
            new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void Present(ShaderRef shader, BufferRef quad, TextureRef tex)
    {
        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = Black(),
        });
        Gfx.draw(6,
            new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = tex,
            },
            new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }
}
