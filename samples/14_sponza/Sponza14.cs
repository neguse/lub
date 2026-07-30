// lub の samples/14_sponza (Haxe 版 Sponza14.hx) の TinyC# 版 entry。
// 実行: lub samples/14_sponza/Sponza14.csproj (transpile + watch + hot reload)
// glTF シーンのマルチパスレンダラ: shadow → G-buffer (MRT) → SSAO →
// lighting → fog → bloom → outline → DoF → motion blur → present。
// load_gltf の mesh / material は動的な Lua table なので、tcs の型消去 cast
// ((Dictionary<string, object>) / (List<object>) 等) で素の table アクセスに
// 写す。Haxe 版の 4 本の並列配列は、List に null を置けない (Lua sequence
// table) ため per-primitive の class SponzaPrim 1 本にまとめる。
using System;
using System.Collections.Generic;
using static @string;

/// <summary>glTF primitive 1 つ分の GPU リソースと material table。</summary>
public class SponzaPrim
{
    public BufferRef? vb;
    public BufferRef? ib;
    public int count;
    public Dictionary<string, object>? mat;
}

public static class Sponza14
{
    const double MODEL_SCALE = 0.002;
    const int SHADOW_SIZE = 2048;
    const string ASSET_FULL = "samples/14_sponza/data/Sponza/Sponza.gltf";

    static int rtW = 1280;
    static int rtH = 720;
    static double tAccum = 0.0;

    static int meshVersion = -1;
    static List<SponzaPrim> prims = new List<SponzaPrim>();

    // camera state。cs-lib の Vec3 を static 初期化子で作ると、emit 順
    // (サンプル → cs-lib) の都合で class 定義前の呼び出しになるため、
    // 成分ごとの double で持ち、必要な所でだけ Vec3 を組む。
    static double camEyeX = -1.5;
    static double camEyeY = 0.25;
    static double camEyeZ = 0.0;
    static double camYaw = 1.5708;
    static double camPitch = 0.0;
    static Mat4? prevViewProj;
    static double pcEyeX = -1.5;
    static double pcEyeY = 0.25;
    static double pcEyeZ = 0.0;
    static double pcYaw = 1.5708;
    static double pcPitch = 0.0;

    static List<double> quadVerts = new List<double>
    {
        -1, -1, 0, 0,
         1, -1, 1, 0,
         1,  1, 1, 1,
        -1, -1, 0, 0,
         1,  1, 1, 1,
        -1,  1, 0, 1,
    };

    static List<double> quadVertsFlip = new List<double>
    {
        -1, -1, 0, 1,
         1, -1, 1, 1,
         1,  1, 1, 0,
        -1, -1, 0, 1,
         1,  1, 1, 0,
        -1,  1, 0, 0,
    };

    static List<double> whitePx = new List<double> { 255, 255, 255, 255 };
    static List<double> normalPx = new List<double> { 128, 128, 255, 255 };

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

    public static void onFrame(double dt)
    {
        tAccum = tAccum + dt;
        Gfx.size(out var w, out var h);
        rtW = w;
        rtH = h;

        var gShader = Shader2("sponza_gbuffer",
            "14_sponza_gbuffer.vs.slang", "14_sponza_gbuffer.fs.slang");
        var shadowShader = Shader2("sponza_shadow",
            "14_sponza_shadow.vs.slang", "14_sponza_shadow.fs.slang");
        var ssaoShader = Shader2("sponza_ssao",
            "14_sponza_ssao.vs.slang", "14_sponza_ssao.fs.slang");
        var lightShader = Shader2("sponza_lighting",
            "14_sponza_light.vs.slang", "14_sponza_light.fs.slang");
        var copyShader = FsShader("sponza_copy", "14_sponza_copy.fs.slang");
        var pShader = FsShader("sponza_present", "14_sponza_present.fs.slang");
        var fogShader = FsShader("sponza_fog", "14_sponza_fog.fs.slang");
        var brightShader = FsShader("sponza_bright",
            "14_sponza_bright.fs.slang");
        var blurHShader = FsShader("sponza_blurh",
            "14_sponza_blur_h.fs.slang");
        var blurVShader = FsShader("sponza_blurv",
            "14_sponza_blur_v.fs.slang");
        var combineShader = FsShader("sponza_combine",
            "14_sponza_combine.fs.slang");
        var outlineShader = FsShader("sponza_outline",
            "14_sponza_outline.fs.slang");
        var dofShader = FsShader("sponza_dof", "14_sponza_dof.fs.slang");
        var motionShader = Shader2("sponza_motion",
            "14_sponza_motion.vs.slang", "14_sponza_motion.fs.slang");
        var screenShader = Shader2("sponza_screen",
            "14_sponza_screen.vs.slang", "14_sponza_screen.fs.slang");
        if (gShader == null || shadowShader == null || ssaoShader == null
            || lightShader == null || copyShader == null || pShader == null
            || fogShader == null || brightShader == null
            || blurHShader == null || blurVShader == null
            || combineShader == null || outlineShader == null
            || dofShader == null || motionShader == null
            || screenShader == null)
        {
            return;
        }

        Io.load_gltf(ASSET_FULL, out var mesh, out var meshVer, out _, out _);
        if (mesh == null) return;
        EnsureMesh(mesh, meshVer);

        var gAlbedo = Target("sponza_g_albedo", rtW, rtH, Gfx.RGBA8,
            Gfx.NEAREST);
        var gNormal = Target("sponza_g_normal", rtW, rtH, Gfx.RGBA16F,
            Gfx.NEAREST);
        var gPosition = Target("sponza_g_position", rtW, rtH, Gfx.RGBA16F,
            Gfx.NEAREST);
        var gDepth = Target("sponza_g_depth", rtW, rtH, Gfx.DEPTH32F,
            Gfx.NEAREST);
        var aoTex = Target("sponza_ao", rtW, rtH, Gfx.RGBA8, Gfx.LINEAR);
        var shadowMap = Target("sponza_shadow_map", SHADOW_SIZE, SHADOW_SIZE,
            Gfx.RGBA8, Gfx.NEAREST);
        var shadowDepth = Target("sponza_shadow_depth", SHADOW_SIZE,
            SHADOW_SIZE, Gfx.DEPTH32F, Gfx.NEAREST);
        var texA = Target("sponza_texA", rtW, rtH, Gfx.RGBA16F, Gfx.LINEAR);
        var texB = Target("sponza_texB", rtW, rtH, Gfx.RGBA16F, Gfx.LINEAR);
        var bloomA = Target("sponza_bloomA", rtW, rtH, Gfx.RGBA16F,
            Gfx.LINEAR);
        var bloomB = Target("sponza_bloomB", rtW, rtH, Gfx.RGBA16F,
            Gfx.LINEAR);
        var quad = Gfx.use_buffer("sponza_quad", Gfx.VERTEX, quadVerts, 1);
        var quadF = Gfx.use_buffer("sponza_quadF", Gfx.VERTEX, quadVertsFlip,
            1);
        if (gAlbedo == null || gNormal == null || gPosition == null
            || gDepth == null || aoTex == null || shadowMap == null
            || shadowDepth == null || texA == null || texB == null
            || bloomA == null || bloomB == null || quad == null
            || quadF == null)
        {
            return;
        }

        var view = UpdateCamera(dt);
        var proj = Mat4.perspectiveLh(55.0, (double)rtW / rtH, 0.05, 80.0);
        proj.m[5] = -proj.m[5];
        var model = Mat4.scaleTrans(MODEL_SCALE, new Vec3(0.0, 0.0, 0.0));

        var worldLight = new Vec3(-0.42, 0.92, -0.32).normalize();
        var lightTarget = new Vec3(0.0, 1.1, 0.0);
        var lightEye = new Vec3(lightTarget.x + worldLight.x * 7.0,
            lightTarget.y + worldLight.y * 7.0,
            lightTarget.z + worldLight.z * 7.0);
        var lightView = Mat4.lookAtLh(lightEye, lightTarget,
            new Vec3(0, 1, 0));
        var lightMvp = Mat4.orthoLh(8.0, 8.0, 0.1, 15.0).mul(lightView);
        var camEye = new Vec3(camEyeX, camEyeY, camEyeZ);
        var invView = view.rigidInverse(camEye);
        var viewToLight = lightMvp.mul(invView);

        var viewProj = proj.mul(view);
        var pvp = prevViewProj;
        var reproj = (pvp == null ? viewProj : pvp).mul(invView);
        prevViewProj = viewProj;
        var camMoved = CameraMoved();

        ShadowPass(shadowShader, shadowMap, shadowDepth, model, lightMvp);
        GeometryPass(gShader, gAlbedo, gNormal, gPosition, gDepth, proj,
            view, model, lightMvp);
        SsaoPass(aoTex, ssaoShader, quadF, gNormal, gPosition, proj.m[0],
            proj.m[5]);
        LightingPass(texA, lightShader, quadF, gAlbedo, gNormal, gPosition,
            shadowMap, aoTex, view, viewToLight);

        BlitFog(texB, fogShader, quadF, texA, gPosition);
        Blit(bloomA, brightShader, quadF, texB);
        Blit(bloomB, blurHShader, quadF, bloomA);
        Blit(bloomA, blurVShader, quadF, bloomB);
        BlitCombine(texA, combineShader, quadF, texB, bloomA);

        if (os.getenv("LUB_SPONZA_NO_OUTLINE") == null)
        {
            BlitOutline(texB, outlineShader, quadF, texA, gNormal, gPosition);
        }
        else
        {
            Blit(texB, copyShader, quadF, texA);
        }

        if (os.getenv("LUB_SPONZA_NO_DOF") == null)
        {
            Blit(bloomB, blurHShader, quadF, texB);
            Blit(bloomA, blurVShader, quadF, bloomB);
            BlitDof(texA, dofShader, quadF, texB, bloomA, gPosition);
        }
        else
        {
            Blit(texA, copyShader, quadF, texB);
        }

        var beauty = texA;
        var outTex = texB;
        if (camMoved && os.getenv("LUB_SPONZA_NO_MOTION") == null)
        {
            MotionPass(texB, motionShader, quadF, texA, gPosition, reproj);
            beauty = texB;
            outTex = texA;
        }

        var mode = SponzaMode();
        var screenSrc = beauty;
        if (mode == 1 || mode == 4)
        {
            screenSrc = gAlbedo;
        }
        else if (mode == 2 || mode == 5)
        {
            screenSrc = gNormal;
        }
        else if (mode == 3)
        {
            screenSrc = gPosition;
        }
        else if (mode == 6)
        {
            screenSrc = aoTex;
        }
        else if (mode == 7)
        {
            screenSrc = shadowMap;
        }

        ScreenPass(outTex, screenShader, quadF, screenSrc, mode);
        Present(pShader, quad, outTex);
    }

    static void EnsureMesh(object mesh, int version)
    {
        if (meshVersion == version) return;
        meshVersion = version;
        prims = new List<SponzaPrim>();

        var meshTbl = (Dictionary<string, object>)mesh;
        var primList = (List<object>)meshTbl["primitives"];
        var n = (int)meshTbl["primitive_count"];
        for (var i = 0; i < n; i++)
        {
            // Haxe 版の prims[i + 1] (Lua 1-based)。List<object> の
            // 0-based index は tcs が +1 して emit する。
            var prim = (Dictionary<string, object>)primList[i];
            var verts = Io.interleave_pnut(prim);
            var p = new SponzaPrim();
            p.vb = Gfx.use_buffer("sponza_vb_" + i, Gfx.VERTEX, verts,
                version);
            if (prim["indices"] != null && (int)prim["index_count"] > 0)
            {
                p.ib = Gfx.use_buffer("sponza_ib_" + i, Gfx.INDEX,
                    (List<double>)prim["indices"], version);
                p.count = (int)prim["index_count"];
            }
            else
            {
                p.count = (int)prim["vert_count"];
            }
            p.mat = (Dictionary<string, object>?)prim["material"];
            prims.Add(p);
        }
    }

    static void ShadowPass(ShaderRef shader, TextureRef shadowMap,
        TextureRef shadowDepth, Mat4 model, Mat4 lightMvp)
    {
        Gfx.begin_pass(new PassOpts
        {
            target = shadowMap,
            depth_target = shadowDepth,
            clear_color = new double[] { 1.0, 1.0, 1.0, 1.0 },
            clear_depth = 1.0,
        });
        var lmvp = lightMvp.m;
        var mv = model.m;
        foreach (var p in prims)
        {
            var vb = p.vb;
            if (vb == null) continue;
            var mat = p.mat;
            var baseTex = MaterialTexture(MatPath(mat, "base_color_path"),
                "bc", whitePx);
            if (baseTex == null) continue;
            var bindings = new Dictionary<string, object>
            {
                ["verts"] = vb,
                ["base_color"] = baseTex,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["light_mvp"] = lmvp,
                    ["model"] = mv,
                    ["base_color_factor"] = BaseColorFactor(mat),
                    ["material"] = MaterialParams(mat),
                },
            };
            var ib = p.ib;
            if (ib != null) bindings["indices"] = ib;
            Gfx.draw(p.count, bindings, new DrawOpts
            {
                shader = shader,
                depth = true,
                depth_write = true,
                cull = Gfx.NONE,
            });
        }
        Gfx.end_pass();
    }

    static void GeometryPass(ShaderRef shader, TextureRef gAlbedo,
        TextureRef gNormal, TextureRef gPosition, TextureRef gDepth,
        Mat4 proj, Mat4 view, Mat4 model, Mat4 lightMvp)
    {
        Gfx.begin_pass(new PassOpts
        {
            targets = new List<TextureRef> { gAlbedo, gNormal, gPosition },
            depth_target = gDepth,
            clear_colors = new List<double[]>
            {
                new double[] { 0.0, 0.0, 0.0, 1.0 },
                new double[] { 0.5, 0.5, 1.0, 0.0 },
                new double[] { 0.0, 0.0, 0.0, 0.0 },
            },
            clear_depth = 1.0,
        });

        var pv = proj.m;
        var vv = view.m;
        var mv = model.m;
        var lmvp = lightMvp.m;
        foreach (var p in prims)
        {
            var vb = p.vb;
            if (vb == null) continue;
            var mat = p.mat;
            var baseTex = MaterialTexture(MatPath(mat, "base_color_path"),
                "bc", whitePx);
            var mrTex = MaterialTexture(
                MatPath(mat, "metallic_roughness_path"), "mr", whitePx);
            var nTex = MaterialTexture(MatPath(mat, "normal_path"), "n",
                normalPx);
            if (baseTex == null || mrTex == null || nTex == null) continue;
            var bindings = new Dictionary<string, object>
            {
                ["verts"] = vb,
                ["base_color"] = baseTex,
                ["metallic_roughness"] = mrTex,
                ["normal_map"] = nTex,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["proj"] = pv,
                    ["view"] = vv,
                    ["model"] = mv,
                    ["light_mvp"] = lmvp,
                    ["base_color_factor"] = BaseColorFactor(mat),
                    ["material"] = MaterialParams(mat),
                    ["normal_params"] = NormalParams(mat),
                },
            };
            var ib = p.ib;
            if (ib != null) bindings["indices"] = ib;
            Gfx.draw(p.count, bindings, new DrawOpts
            {
                shader = shader,
                depth = true,
                depth_write = true,
                cull = Gfx.NONE,
            });
        }
        Gfx.end_pass();
    }

    static void LightingPass(TextureRef targ, ShaderRef shader,
        BufferRef quad, TextureRef gAlbedo, TextureRef gNormal,
        TextureRef gPosition, TextureRef shadowMap, TextureRef aoTex,
        Mat4 view, Mat4 viewToLight)
    {
        var l0 = view.mat3MulVec3(new Vec3(-0.42, 0.92, -0.32).normalize())
            .normalize();
        var l1 = view.mat3MulVec3(new Vec3(0.58, 0.35, 0.22).normalize())
            .normalize();
        Gfx.begin_pass(new PassOpts
        {
            target = targ,
            clear_color = new double[] { 0.0, 0.0, 0.0, 1.0 },
        });
        var vl = viewToLight.m;
        Gfx.draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["g_albedo"] = gAlbedo,
            ["g_normal"] = gNormal,
            ["g_position"] = gPosition,
            ["shadow_map"] = shadowMap,
            ["ao_map"] = aoTex,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["light0"] = new List<double> { l0.x, l0.y, l0.z, 5.6 },
                ["light1"] = new List<double> { l1.x, l1.y, l1.z, 0.7 },
                ["params"] = new List<double> { 1.0, 0.050, 0.82, 0.85 },
                ["vl0"] = new List<double> { vl[0], vl[1], vl[2], vl[3] },
                ["vl1"] = new List<double> { vl[4], vl[5], vl[6], vl[7] },
                ["vl2"] = new List<double> { vl[8], vl[9], vl[10], vl[11] },
                ["vl3"] = new List<double> { vl[12], vl[13], vl[14], vl[15] },
            },
        }, new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void Blit(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = tex,
        }, new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void BlitFog(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef gPosition)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = tex,
            ["gpos"] = gPosition,
        }, new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void BlitCombine(TextureRef targ, ShaderRef shader,
        BufferRef quad, TextureRef baseTex, TextureRef bloom)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = baseTex,
            ["bloom"] = bloom,
        }, new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void BlitOutline(TextureRef targ, ShaderRef shader,
        BufferRef quad, TextureRef tex, TextureRef gNormal,
        TextureRef gPosition)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = tex,
            ["gnormal"] = gNormal,
            ["gpos"] = gPosition,
        }, new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void BlitDof(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef blurred, TextureRef gPosition)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = tex,
            ["blurred"] = blurred,
            ["gpos"] = gPosition,
        }, new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void SsaoPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef gNormal, TextureRef gPosition, double p00, double p11)
    {
        Gfx.begin_pass(new PassOpts
        {
            target = targ,
            clear_color = new double[] { 1.0, 1.0, 1.0, 1.0 },
        });
        Gfx.draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["gnormal"] = gNormal,
            ["gpos"] = gPosition,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["params"] = new List<double> { p00, p11, 0.0, 0.0 },
            },
        }, new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void MotionPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef gPosition, Mat4 m)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        var mm = m.m;
        Gfx.draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = tex,
            ["gpos"] = gPosition,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["r0"] = new List<double> { mm[0], mm[1], mm[2], mm[3] },
                ["r1"] = new List<double> { mm[4], mm[5], mm[6], mm[7] },
                ["r2"] = new List<double> { mm[8], mm[9], mm[10], mm[11] },
                ["r3"] = new List<double> { mm[12], mm[13], mm[14], mm[15] },
            },
        }, new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void ScreenPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, int mode)
    {
        Gfx.begin_pass(new PassOpts { target = targ, clear_color = Black() });
        Gfx.draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = tex,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["params"] = new List<double> { mode, 0.0, 0.0, 0.0 },
            },
        }, new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static void Present(ShaderRef shader, BufferRef quad, TextureRef tex)
    {
        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = Black(),
        });
        Gfx.draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = tex,
        }, new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }

    static TextureRef? MaterialTexture(object? path, string suffix,
        List<double> fallback)
    {
        if (path != null)
        {
            var p = (string)path;
            Png.load(p, out var bytes, out var pw, out var ph, out var pfmt,
                out _, out var pver, out _, out _);
            if (bytes != null)
            {
                return Gfx.use_texture("sponza_tex_" + suffix + "_" + p, pw,
                    ph, pfmt, bytes, pver,
                    new TextureOpts { filter = Gfx.LINEAR, wrap = Gfx.REPEAT });
            }
        }
        return Gfx.use_texture("sponza_default_" + suffix, 1, 1, Gfx.RGBA8,
            fallback, 1, new TextureOpts { filter = Gfx.LINEAR, wrap = Gfx.REPEAT });
    }

    /// <summary>material table のキーを読む。mat 無し / キー無しは null。</summary>
    static object? MatPath(Dictionary<string, object>? mat, string key)
    {
        if (mat == null) return null;
        return mat[key];
    }

    static List<double> BaseColorFactor(Dictionary<string, object>? mat)
    {
        var bc = MatPath(mat, "base_color_factor");
        // Haxe 版の tableFloat(bc, 1..4) は Lua 1-based の直書き添字。
        // List<object> は 0-based (tcs が +1) なので 0..3 になる。
        return new List<double>
        {
            TableFloat(bc, 0, 1.0),
            TableFloat(bc, 1, 1.0),
            TableFloat(bc, 2, 1.0),
            TableFloat(bc, 3, 1.0),
        };
    }

    static List<double> MaterialParams(Dictionary<string, object>? mat)
    {
        var metallic = MatNumber(mat, "metallic_factor", 1.0);
        var roughness = MatNumber(mat, "roughness_factor", 1.0);
        var cutoff = MatNumber(mat, "alpha_cutoff", 0.5);
        var alphaMode = MatNumber(mat, "alpha_mode", 0.0);
        return new List<double> { metallic, roughness, cutoff, alphaMode };
    }

    static List<double> NormalParams(Dictionary<string, object>? mat)
    {
        var scale = MatNumber(mat, "normal_scale", 1.0);
        return new List<double> { scale, 0.0, 0.0, 0.0 };
    }

    static double MatNumber(Dictionary<string, object>? mat, string key,
        double def)
    {
        if (mat == null) return def;
        var v = mat[key];
        if (v == null) return def;
        return (double)v;
    }

    static double TableFloat(object? t, int i, double def)
    {
        if (t == null) return def;
        var list = (List<object>)t;
        var v = list[i];
        if (v == null) return def;
        return (double)v;
    }

    static TextureRef? Target(string key, int w, int h, int fmt, int filter)
    {
        var ver = w * 100000 + h * 100 + fmt;
        return Gfx.use_texture(key, w, h, fmt, null, ver,
            new TextureOpts { target = true, filter = filter, wrap = Gfx.CLAMP });
    }

    static ShaderRef? FsShader(string key, string fsPath)
    {
        return Shader2(key, "14_sponza_quad.vs.slang", fsPath);
    }

    static ShaderRef? Shader2(string key, string vsPath, string fsPath)
    {
        Io.load_text("samples/14_sponza/data/" + vsPath,
            out var v, out var vv, out _, out _);
        Io.load_text("samples/14_sponza/data/" + fsPath,
            out var f, out var fv, out _, out _);
        if (v == null || f == null) return null;
        return Gfx.use_shader(key, v, f, vv * 31 + fv);
    }

    static double[] Black()
    {
        return new double[] { 0.0, 0.0, 0.0, 1.0 };
    }

    static int SponzaMode()
    {
        var s = os.getenv("LUB_SPONZA_MODE");
        if (s == null) return 0;
        switch (s.ToLower())
        {
            case "albedo":
                return 1;
            case "normal":
                return 2;
            case "depth":
                return 3;
            case "roughness":
                return 4;
            case "metallic":
                return 5;
            case "ao":
                return 6;
            case "shadow":
                return 7;
            case "beauty":
                return 8;
        }
        var n = ParseNumber(s);
        if (n == null) return 0;
        return (int)(double)n;
    }

    // tcs に tonumber / Parse 相当が無いので、env 値 ([-+]?digits[.digits])
    // を string.byte 走査で読む。読めない文字列は null (Haxe 版は NaN /
    // null になる系。ここでは代入をスキップする)。
    static double? ParseNumber(string s)
    {
        var str = s.Trim();
        var n = len(str);
        if (n == 0) return null;
        var i = 1;
        var sign = 1.0;
        var c0 = @byte(str, 1);
        if (c0 == 43)
        {
            i = 2;
        }
        else if (c0 == 45)
        {
            sign = -1.0;
            i = 2;
        }
        var any = false;
        var value = 0.0;
        while (i <= n)
        {
            var d = @byte(str, i);
            if (d < 48 || d > 57) break;
            value = value * 10.0 + (d - 48);
            any = true;
            i = i + 1;
        }
        if (i <= n && @byte(str, i) == 46)
        {
            i = i + 1;
            var scale = 1.0;
            while (i <= n)
            {
                var d = @byte(str, i);
                if (d < 48 || d > 57) break;
                scale = scale * 0.1;
                value = value + (d - 48) * scale;
                any = true;
                i = i + 1;
            }
        }
        if (!any || i <= n) return null;
        return sign * value;
    }

    static bool CameraMoved()
    {
        var moved = Math.Abs(camEyeX - pcEyeX) + Math.Abs(camEyeY - pcEyeY)
            + Math.Abs(camEyeZ - pcEyeZ) + Math.Abs(camYaw - pcYaw)
            + Math.Abs(camPitch - pcPitch) > 1e-6;
        pcEyeX = camEyeX;
        pcEyeY = camEyeY;
        pcEyeZ = camEyeZ;
        pcYaw = camYaw;
        pcPitch = camPitch;
        return moved;
    }

    static Mat4 UpdateCamera(double dt)
    {
        var camStr = os.getenv("LUB_SPONZA_CAM");
        if (camStr != null)
        {
            var p = camStr.Split(",");
            if (p.Length >= 5)
            {
                var yaw = ParseNumber(p[0]);
                var pitch = ParseNumber(p[1]);
                var ex = ParseNumber(p[2]);
                var ey = ParseNumber(p[3]);
                var ez = ParseNumber(p[4]);
                if (yaw != null) camYaw = (double)yaw;
                if (pitch != null) camPitch = (double)pitch;
                if (ex != null) camEyeX = (double)ex;
                if (ey != null) camEyeY = (double)ey;
                if (ez != null) camEyeZ = (double)ez;
            }
        }
        if (os.getenv("LUB_SPONZA_SPIN") != null)
        {
            camYaw = Math.Sin(tAccum * 0.25) * 0.32;
        }

        Input.mouse_delta(out var mdx, out var mdy);
        if (Input.mouse_down(1))
        {
            camYaw = camYaw + mdx * 0.003;
            camPitch = camPitch - mdy * 0.003;
            if (camPitch > 1.45) camPitch = 1.45;
            if (camPitch < -1.45) camPitch = -1.45;
        }

        var up = new Vec3(0, 1, 0);
        var fwd = ForwardDir();
        var right = up.cross(fwd).normalize();
        var spd = 2.6 * dt;
        if (Input.key_down("w"))
        {
            camEyeX = camEyeX + fwd.x * spd;
            camEyeY = camEyeY + fwd.y * spd;
            camEyeZ = camEyeZ + fwd.z * spd;
        }
        if (Input.key_down("s"))
        {
            camEyeX = camEyeX - fwd.x * spd;
            camEyeY = camEyeY - fwd.y * spd;
            camEyeZ = camEyeZ - fwd.z * spd;
        }
        if (Input.key_down("d"))
        {
            camEyeX = camEyeX + right.x * spd;
            camEyeY = camEyeY + right.y * spd;
            camEyeZ = camEyeZ + right.z * spd;
        }
        if (Input.key_down("a"))
        {
            camEyeX = camEyeX - right.x * spd;
            camEyeY = camEyeY - right.y * spd;
            camEyeZ = camEyeZ - right.z * spd;
        }
        if (Input.key_down("e")) camEyeY = camEyeY + spd;
        if (Input.key_down("q")) camEyeY = camEyeY - spd;

        var eye = new Vec3(camEyeX, camEyeY, camEyeZ);
        var target = new Vec3(camEyeX + fwd.x, camEyeY + fwd.y,
            camEyeZ + fwd.z);
        return Mat4.lookAtLh(eye, target, up);
    }

    static Vec3 ForwardDir()
    {
        var cp = Math.Cos(camPitch);
        return new Vec3(Math.Sin(camYaw) * cp, Math.Sin(camPitch),
            Math.Cos(camYaw) * cp);
    }
}
