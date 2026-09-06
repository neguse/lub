// lub の samples/14_sponza の entry。
// 実行: lub samples/14_sponza/Sponza14.csproj (transpile + watch + hot reload)
// glTF シーンのマルチパスレンダラ: shadow → G-buffer (MRT) → SSAO →
// lighting → fog → bloom → outline → DoF → motion blur → present。
// load_gltf の mesh / material は typed な GltfMesh / GltfMaterial で受ける。
// List に null を置けない (Lua sequence table) ため、primitive ごとの情報は
// class SponzaPrim 1 本にまとめる。
using System;
using System.Collections.Generic;
using static Lub;

/// <summary>glTF primitive 1 つ分の GPU リソースと material table。</summary>
public class SponzaPrim
{
    public BufferRef? Vb;
    public BufferRef? Ib;
    public int Count;
    public GltfMaterial? Mat;
}

public static class Sponza14
{
    const float modelScale = 0.002f;
    const int shadowSize = 2048;
    const string assetFull = "samples/14_sponza/data/Sponza/Sponza.gltf";

    static int rtW = 1280;
    static int rtH = 720;
    static float tAccum = 0.0f;

    static int meshVersion = -1;
    static List<SponzaPrim> prims = new List<SponzaPrim>();

    // camera state。cs-lib の Vec3 を static 初期化子で作ると、emit 順
    // (サンプル → cs-lib) の都合で class 定義前の呼び出しになるため、
    // 成分ごとの float で持ち、必要な所でだけ Vec3 を組む。
    static float camEyeX = -1.5f;
    static float camEyeY = 0.25f;
    static float camEyeZ = 0.0f;
    static float camYaw = 1.5708f;
    static float camPitch = 0.0f;
    static Mat4? prevViewProj;
    static float pcEyeX = -1.5f;
    static float pcEyeY = 0.25f;
    static float pcEyeZ = 0.0f;
    static float pcYaw = 1.5708f;
    static float pcPitch = 0.0f;

    static List<float> quadVerts = new List<float>
    {
        -1, -1, 0, 0,
         1, -1, 1, 0,
         1,  1, 1, 1,
        -1, -1, 0, 0,
         1,  1, 1, 1,
        -1,  1, 0, 1,
    };

    static List<float> quadVertsFlip = new List<float>
    {
        -1, -1, 0, 1,
         1, -1, 1, 1,
         1,  1, 1, 0,
        -1, -1, 0, 1,
         1,  1, 1, 0,
        -1,  1, 0, 0,
    };

    static List<int> whitePx = new List<int> { 255, 255, 255, 255 };
    static List<int> normalPx = new List<int> { 128, 128, 255, 255 };

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
        tAccum = tAccum + dt;
        Gfx.Size(out var w, out var h);
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

        Io.LoadGltf(assetFull, out var mesh, out var meshVer, out _, out _);
        if (mesh == null) return;
        EnsureMesh(mesh, meshVer);

        var gAlbedo = Target("sponza_g_albedo", rtW, rtH, Gfx.PixelFormat.Rgba8,
            Gfx.Filter.Nearest);
        var gNormal = Target("sponza_g_normal", rtW, rtH, Gfx.PixelFormat.Rgba16f,
            Gfx.Filter.Nearest);
        var gPosition = Target("sponza_g_position", rtW, rtH, Gfx.PixelFormat.Rgba16f,
            Gfx.Filter.Nearest);
        var gDepth = Target("sponza_g_depth", rtW, rtH, Gfx.PixelFormat.Depth32f,
            Gfx.Filter.Nearest);
        var aoTex = Target("sponza_ao", rtW, rtH, Gfx.PixelFormat.Rgba8, Gfx.Filter.Linear);
        var shadowMap = Target("sponza_shadow_map", shadowSize, shadowSize,
            Gfx.PixelFormat.Rgba8, Gfx.Filter.Nearest);
        var shadowDepth = Target("sponza_shadow_depth", shadowSize,
            shadowSize, Gfx.PixelFormat.Depth32f, Gfx.Filter.Nearest);
        var texA = Target("sponza_texA", rtW, rtH, Gfx.PixelFormat.Rgba16f, Gfx.Filter.Linear);
        var texB = Target("sponza_texB", rtW, rtH, Gfx.PixelFormat.Rgba16f, Gfx.Filter.Linear);
        var bloomA = Target("sponza_bloomA", rtW, rtH, Gfx.PixelFormat.Rgba16f,
            Gfx.Filter.Linear);
        var bloomB = Target("sponza_bloomB", rtW, rtH, Gfx.PixelFormat.Rgba16f,
            Gfx.Filter.Linear);
        var quad = Gfx.UseBuffer("sponza_quad", Gfx.BufferType.Vertex, quadVerts, 1);
        var quadF = Gfx.UseBuffer("sponza_quadF", Gfx.BufferType.Vertex, quadVertsFlip,
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
        var proj = Mat4.PerspectiveLh(55.0f, (float)rtW / rtH, 0.05f, 80.0f);
        proj.M[5] = -proj.M[5];
        var model = Mat4.ScaleTrans(modelScale, new Vec3(0.0f, 0.0f, 0.0f));

        var worldLight = new Vec3(-0.42f, 0.92f, -0.32f).Normalize();
        var lightTarget = new Vec3(0.0f, 1.1f, 0.0f);
        var lightEye = new Vec3(lightTarget.X + worldLight.X * 7.0f,
            lightTarget.Y + worldLight.Y * 7.0f,
            lightTarget.Z + worldLight.Z * 7.0f);
        var lightView = Mat4.LookAtLh(lightEye, lightTarget,
            new Vec3(0, 1, 0));
        var lightMvp = Mat4.OrthoLh(8.0f, 8.0f, 0.1f, 15.0f).Mul(lightView);
        var camEye = new Vec3(camEyeX, camEyeY, camEyeZ);
        var invView = view.RigidInverse(camEye);
        var viewToLight = lightMvp.Mul(invView);

        var viewProj = proj.Mul(view);
        var pvp = prevViewProj;
        var reproj = (pvp == null ? viewProj : pvp).Mul(invView);
        prevViewProj = viewProj;
        var camMoved = CameraMoved();

        ShadowPass(shadowShader, shadowMap, shadowDepth, model, lightMvp);
        GeometryPass(gShader, gAlbedo, gNormal, gPosition, gDepth, proj,
            view, model, lightMvp);
        SsaoPass(aoTex, ssaoShader, quadF, gNormal, gPosition, proj.M[0],
            proj.M[5]);
        LightingPass(texA, lightShader, quadF, gAlbedo, gNormal, gPosition,
            shadowMap, aoTex, view, viewToLight);

        BlitFog(texB, fogShader, quadF, texA, gPosition);
        Blit(bloomA, brightShader, quadF, texB);
        Blit(bloomB, blurHShader, quadF, bloomA);
        Blit(bloomA, blurVShader, quadF, bloomB);
        BlitCombine(texA, combineShader, quadF, texB, bloomA);

        if (Environment.GetEnvironmentVariable("LUB_SPONZA_NO_OUTLINE") == null)
        {
            BlitOutline(texB, outlineShader, quadF, texA, gNormal, gPosition);
        }
        else
        {
            Blit(texB, copyShader, quadF, texA);
        }

        if (Environment.GetEnvironmentVariable("LUB_SPONZA_NO_DOF") == null)
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
        if (camMoved && Environment.GetEnvironmentVariable("LUB_SPONZA_NO_MOTION") == null)
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

    static void EnsureMesh(GltfMesh mesh, int version)
    {
        if (meshVersion == version) return;
        meshVersion = version;
        prims = new List<SponzaPrim>();

        var n = mesh.Primitives.Count;
        for (var i = 0; i < n; i++)
        {
            var prim = mesh.Primitives[i];
            var verts = Io.InterleavePnut(prim);
            var p = new SponzaPrim();
            p.Vb = Gfx.UseBuffer("sponza_vb_" + i, Gfx.BufferType.Vertex, verts,
                version);
            if (prim.IndexCount > 0)
            {
                p.Ib = Gfx.UseBufferInts("sponza_ib_" + i, Gfx.BufferType.Index, prim.Indices, version);
                p.Count = prim.IndexCount;
            }
            else
            {
                p.Count = prim.VertCount;
            }
            p.Mat = prim.Material;
            prims.Add(p);
        }
    }

    static void ShadowPass(ShaderRef shader, TextureRef shadowMap,
        TextureRef shadowDepth, Mat4 model, Mat4 lightMvp)
    {
        Gfx.BeginPass(new PassOpts
        {
            Target = shadowMap,
            DepthTarget = shadowDepth,
            ClearColor = new float[] { 1.0f, 1.0f, 1.0f, 1.0f },
            ClearDepth = 1.0f,
        });
        var lmvp = lightMvp.M;
        var mv = model.M;
        foreach (var p in prims)
        {
            var vb = p.Vb;
            if (vb == null) continue;
            var mat = p.Mat;
            var baseTex = MaterialTexture(MatBase(mat),
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
            var ib = p.Ib;
            if (ib != null) bindings["indices"] = ib;
            Gfx.Draw(p.Count, bindings, new DrawOpts
            {
                Shader = shader,
                Depth = true,
                DepthWrite = true,
                Cull = Gfx.Cull.None,
            });
        }
        Gfx.EndPass();
    }

    static void GeometryPass(ShaderRef shader, TextureRef gAlbedo,
        TextureRef gNormal, TextureRef gPosition, TextureRef gDepth,
        Mat4 proj, Mat4 view, Mat4 model, Mat4 lightMvp)
    {
        Gfx.BeginPass(new PassOpts
        {
            Targets = new List<TextureRef> { gAlbedo, gNormal, gPosition },
            DepthTarget = gDepth,
            ClearColors = new List<float[]>
            {
                new float[] { 0.0f, 0.0f, 0.0f, 1.0f },
                new float[] { 0.5f, 0.5f, 1.0f, 0.0f },
                new float[] { 0.0f, 0.0f, 0.0f, 0.0f },
            },
            ClearDepth = 1.0f,
        });

        var pv = proj.M;
        var vv = view.M;
        var mv = model.M;
        var lmvp = lightMvp.M;
        foreach (var p in prims)
        {
            var vb = p.Vb;
            if (vb == null) continue;
            var mat = p.Mat;
            var baseTex = MaterialTexture(MatBase(mat),
                "bc", whitePx);
            var mrTex = MaterialTexture(
                MatMr(mat), "mr", whitePx);
            var nTex = MaterialTexture(MatNormal(mat), "n",
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
            var ib = p.Ib;
            if (ib != null) bindings["indices"] = ib;
            Gfx.Draw(p.Count, bindings, new DrawOpts
            {
                Shader = shader,
                Depth = true,
                DepthWrite = true,
                Cull = Gfx.Cull.None,
            });
        }
        Gfx.EndPass();
    }

    static void LightingPass(TextureRef targ, ShaderRef shader,
        BufferRef quad, TextureRef gAlbedo, TextureRef gNormal,
        TextureRef gPosition, TextureRef shadowMap, TextureRef aoTex,
        Mat4 view, Mat4 viewToLight)
    {
        var l0 = view.Mat3MulVec3(new Vec3(-0.42f, 0.92f, -0.32f).Normalize())
            .Normalize();
        var l1 = view.Mat3MulVec3(new Vec3(0.58f, 0.35f, 0.22f).Normalize())
            .Normalize();
        Gfx.BeginPass(new PassOpts
        {
            Target = targ,
            ClearColor = new float[] { 0.0f, 0.0f, 0.0f, 1.0f },
        });
        var vl = viewToLight.M;
        Gfx.Draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["g_albedo"] = gAlbedo,
            ["g_normal"] = gNormal,
            ["g_position"] = gPosition,
            ["shadow_map"] = shadowMap,
            ["ao_map"] = aoTex,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["light0"] = new List<float> { l0.X, l0.Y, l0.Z, 5.6f },
                ["light1"] = new List<float> { l1.X, l1.Y, l1.Z, 0.7f },
                ["params"] = new List<float> { 1.0f, 0.050f, 0.82f, 0.85f },
                ["vl0"] = new List<float> { vl[0], vl[1], vl[2], vl[3] },
                ["vl1"] = new List<float> { vl[4], vl[5], vl[6], vl[7] },
                ["vl2"] = new List<float> { vl[8], vl[9], vl[10], vl[11] },
                ["vl3"] = new List<float> { vl[12], vl[13], vl[14], vl[15] },
            },
        }, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void Blit(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = tex,
        }, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void BlitFog(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef gPosition)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = tex,
            ["gpos"] = gPosition,
        }, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void BlitCombine(TextureRef targ, ShaderRef shader,
        BufferRef quad, TextureRef baseTex, TextureRef bloom)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = baseTex,
            ["bloom"] = bloom,
        }, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void BlitOutline(TextureRef targ, ShaderRef shader,
        BufferRef quad, TextureRef tex, TextureRef gNormal,
        TextureRef gPosition)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = tex,
            ["gnormal"] = gNormal,
            ["gpos"] = gPosition,
        }, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void BlitDof(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef blurred, TextureRef gPosition)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = tex,
            ["blurred"] = blurred,
            ["gpos"] = gPosition,
        }, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void SsaoPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef gNormal, TextureRef gPosition, float p00, float p11)
    {
        Gfx.BeginPass(new PassOpts
        {
            Target = targ,
            ClearColor = new float[] { 1.0f, 1.0f, 1.0f, 1.0f },
        });
        Gfx.Draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["gnormal"] = gNormal,
            ["gpos"] = gPosition,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["params"] = new List<float> { p00, p11, 0.0f, 0.0f },
            },
        }, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void MotionPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef gPosition, Mat4 m)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        var mm = m.M;
        Gfx.Draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = tex,
            ["gpos"] = gPosition,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["r0"] = new List<float> { mm[0], mm[1], mm[2], mm[3] },
                ["r1"] = new List<float> { mm[4], mm[5], mm[6], mm[7] },
                ["r2"] = new List<float> { mm[8], mm[9], mm[10], mm[11] },
                ["r3"] = new List<float> { mm[12], mm[13], mm[14], mm[15] },
            },
        }, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void ScreenPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, int mode)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        Gfx.Draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = tex,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["params"] = new List<float> { mode, 0.0f, 0.0f, 0.0f },
            },
        }, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void Present(ShaderRef shader, BufferRef quad, TextureRef tex)
    {
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = Black(),
        });
        Gfx.Draw(6, new Dictionary<string, object>
        {
            ["verts"] = quad,
            ["scene"] = tex,
        }, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static TextureRef? MaterialTexture(string? path, string suffix,
        List<int> fallback)
    {
        if (path != null)
        {
            var p = path;
            Png.Load(p, out var bytes, out var pw, out var ph, out var pfmt,
                out _, out var pver, out _, out _);
            if (bytes != null)
            {
                return Gfx.UseTextureBytes("sponza_tex_" + suffix + "_" + p, pw,
                    ph, (Gfx.PixelFormat)pfmt, bytes, pver,
                    new TextureOpts { Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Repeat });
            }
        }
        return Gfx.UseTexture("sponza_default_" + suffix, 1, 1, Gfx.PixelFormat.Rgba8,
            fallback, 1, new TextureOpts { Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Repeat });
    }

    // material 無しは null (default texture)。
    static string? MatBase(GltfMaterial? mat)
    {
        if (mat == null) return null;
        return mat.BaseColorPath;
    }

    static string? MatMr(GltfMaterial? mat)
    {
        if (mat == null) return null;
        return mat.MetallicRoughnessPath;
    }

    static string? MatNormal(GltfMaterial? mat)
    {
        if (mat == null) return null;
        return mat.NormalPath;
    }

    static List<float> BaseColorFactor(GltfMaterial? mat)
    {
        if (mat == null || mat.BaseColorFactor.Count < 4)
            return new List<float> { 1.0f, 1.0f, 1.0f, 1.0f };
        var bc = mat.BaseColorFactor;
        return new List<float> { bc[0], bc[1], bc[2], bc[3] };
    }

    static List<float> MaterialParams(GltfMaterial? mat)
    {
        if (mat == null)
            return new List<float> { 1.0f, 1.0f, 0.5f, 0.0f };
        return new List<float>
        {
            mat.MetallicFactor, mat.RoughnessFactor, mat.AlphaCutoff,
            mat.AlphaMode,
        };
    }

    static List<float> NormalParams(GltfMaterial? mat)
    {
        var scale = mat == null ? 1.0f : mat.NormalScale;
        return new List<float> { scale, 0.0f, 0.0f, 0.0f };
    }

    static TextureRef? Target(string key, int w, int h, Gfx.PixelFormat fmt,
        Gfx.Filter filter)
    {
        var ver = w * 100000 + h * 100 + (int)fmt;
        return Gfx.UseTexture(key, w, h, fmt, null, ver,
            new TextureOpts { Target = true, Filter = filter, Wrap = Gfx.Wrap.Clamp });
    }

    static ShaderRef? FsShader(string key, string fsPath)
    {
        return Shader2(key, "14_sponza_quad.vs.slang", fsPath);
    }

    static ShaderRef? Shader2(string key, string vsPath, string fsPath)
    {
        Io.LoadText("samples/14_sponza/data/" + vsPath,
            out var v, out var vv, out _, out _);
        Io.LoadText("samples/14_sponza/data/" + fsPath,
            out var f, out var fv, out _, out _);
        if (v == null || f == null) return null;
        return Gfx.UseShader(key, v, f, vv * 31 + fv);
    }

    static float[] Black()
    {
        return new float[] { 0.0f, 0.0f, 0.0f, 1.0f };
    }

    static int SponzaMode()
    {
        var s = Environment.GetEnvironmentVariable("LUB_SPONZA_MODE");
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
        return (int)(float)n;
    }

    // env 値 ([-+]?digits[.digits]) を読む。読めない文字列は null
    // (呼び出し側は代入をスキップする)。
    static float? ParseNumber(string s)
    {
        var str = s.Trim();
        var n = str.Length;
        if (n == 0) return null;
        var i = 0;
        var sign = 1.0f;
        var c0 = (int)str[0];
        if (c0 == 43)
        {
            i = 1;
        }
        else if (c0 == 45)
        {
            sign = -1.0f;
            i = 1;
        }
        var any = false;
        var value = 0.0f;
        while (i < n)
        {
            var d = (int)str[i];
            if (d < 48 || d > 57) break;
            value = value * 10.0f + (d - 48);
            any = true;
            i = i + 1;
        }
        if (i < n && (int)str[i] == 46)
        {
            i = i + 1;
            var scale = 1.0f;
            while (i < n)
            {
                var d = (int)str[i];
                if (d < 48 || d > 57) break;
                scale = scale * 0.1f;
                value = value + (d - 48) * scale;
                any = true;
                i = i + 1;
            }
        }
        if (!any || i < n) return null;
        return sign * value;
    }

    static bool CameraMoved()
    {
        var moved = Math.Abs(camEyeX - pcEyeX) + Math.Abs(camEyeY - pcEyeY)
            + Math.Abs(camEyeZ - pcEyeZ) + Math.Abs(camYaw - pcYaw)
            + Math.Abs(camPitch - pcPitch) > 1e-6f;
        pcEyeX = camEyeX;
        pcEyeY = camEyeY;
        pcEyeZ = camEyeZ;
        pcYaw = camYaw;
        pcPitch = camPitch;
        return moved;
    }

    static Mat4 UpdateCamera(float dt)
    {
        var camStr = Environment.GetEnvironmentVariable("LUB_SPONZA_CAM");
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
                if (yaw != null) camYaw = (float)yaw;
                if (pitch != null) camPitch = (float)pitch;
                if (ex != null) camEyeX = (float)ex;
                if (ey != null) camEyeY = (float)ey;
                if (ez != null) camEyeZ = (float)ez;
            }
        }
        if (Environment.GetEnvironmentVariable("LUB_SPONZA_SPIN") != null)
        {
            camYaw = (float)Math.Sin(tAccum * 0.25f) * 0.32f;
        }

        Input.MouseDelta(out var mdx, out var mdy);
        if (Input.MouseDown(1))
        {
            camYaw = camYaw + mdx * 0.003f;
            camPitch = camPitch - mdy * 0.003f;
            if (camPitch > 1.45f) camPitch = 1.45f;
            if (camPitch < -1.45f) camPitch = -1.45f;
        }

        var up = new Vec3(0, 1, 0);
        var fwd = ForwardDir();
        var right = up.Cross(fwd).Normalize();
        var spd = 2.6f * dt;
        if (Input.KeyDown("w"))
        {
            camEyeX = camEyeX + fwd.X * spd;
            camEyeY = camEyeY + fwd.Y * spd;
            camEyeZ = camEyeZ + fwd.Z * spd;
        }
        if (Input.KeyDown("s"))
        {
            camEyeX = camEyeX - fwd.X * spd;
            camEyeY = camEyeY - fwd.Y * spd;
            camEyeZ = camEyeZ - fwd.Z * spd;
        }
        if (Input.KeyDown("d"))
        {
            camEyeX = camEyeX + right.X * spd;
            camEyeY = camEyeY + right.Y * spd;
            camEyeZ = camEyeZ + right.Z * spd;
        }
        if (Input.KeyDown("a"))
        {
            camEyeX = camEyeX - right.X * spd;
            camEyeY = camEyeY - right.Y * spd;
            camEyeZ = camEyeZ - right.Z * spd;
        }
        if (Input.KeyDown("e")) camEyeY = camEyeY + spd;
        if (Input.KeyDown("q")) camEyeY = camEyeY - spd;

        var eye = new Vec3(camEyeX, camEyeY, camEyeZ);
        var target = new Vec3(camEyeX + fwd.X, camEyeY + fwd.Y,
            camEyeZ + fwd.Z);
        return Mat4.LookAtLh(eye, target, up);
    }

    static Vec3 ForwardDir()
    {
        var cp = (float)Math.Cos(camPitch);
        return new Vec3((float)Math.Sin(camYaw) * cp, (float)Math.Sin(camPitch),
            (float)Math.Cos(camYaw) * cp);
    }
}
