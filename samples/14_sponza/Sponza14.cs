// lub の samples/14_sponza の entry。
// 実行: lub samples/14_sponza/Sponza14.csproj (transpile + watch + hot reload) glTF シーンのマルチパスレンダラ: shadow → G-buffer (MRT) → SSAO → lighting → fog → bloom → outline → DoF → motion blur → present。
// Io.LoadGltf の mesh / material は typed な GltfMesh / GltfMaterial で受ける。
using System;
using System.Collections.Generic;
using Lub;
using TinySystem.Text;

/// <summary>glTF primitive 1 つ分の GPU リソースと material table。
/// </summary>
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

    static List<SponzaPrim> prims = new List<SponzaPrim>();

    // camera state。
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
        App.Config(new ConfigOpts { Backend = backend });
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
        DeclareMesh(mesh, meshVer);

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
        var quad = Gfx.UseBuffer("sponza_quad", Gfx.BufferType.Storage, quadVerts, 1);
        var quadF = Gfx.UseBuffer("sponza_quadF", Gfx.BufferType.Storage, quadVertsFlip,
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
        proj.M11 = -proj.M11;
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
        var reproj = (prevViewProj ?? viewProj).Mul(invView);
        prevViewProj = viewProj;
        var camMoved = CameraMoved();

        ShadowPass(shadowShader, shadowMap, shadowDepth, model, lightMvp);
        GeometryPass(gShader, gAlbedo, gNormal, gPosition, gDepth, proj,
            view, model, lightMvp);
        SsaoPass(aoTex, ssaoShader, quadF, gNormal, gPosition, proj.M00,
            proj.M11);
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

    // primitive ごとの buffer を毎フレーム宣言する。
    // 同じ version の buffer が残っていれば頂点を組まずにそれを使い、無ければ (初回、glTF の更新、sweep の後) 組んで宣言する。
    static void DeclareMesh(GltfMesh mesh, int version)
    {
        prims = new List<SponzaPrim>();
        var n = mesh.Primitives.Count;
        for (var i = 0; i < n; i++)
        {
            var prim = mesh.Primitives[i];
            var p = new SponzaPrim();
            var vbKey = "sponza_vb_" + i;
            p.Vb = Gfx.UseBuffer(vbKey, Gfx.BufferType.Storage, null, version)
                ?? Gfx.UseBuffer(vbKey, Gfx.BufferType.Storage, Io.InterleavePnut(prim.Mesh), version);
            if (prim.Mesh.IndexCount > 0)
            {
                var ibKey = "sponza_ib_" + i;
                p.Ib = Gfx.UseBufferInts(ibKey, Gfx.BufferType.Index, null, version)
                    ?? Gfx.UseBufferInts(ibKey, Gfx.BufferType.Index, prim.Mesh.Indices, version);
                p.Count = prim.Mesh.IndexCount;
            }
            else
            {
                p.Count = prim.Mesh.VertCount;
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
        var bindings = new Bindings();
        foreach (var p in prims)
        {
            var vb = p.Vb;
            if (vb == null) continue;
            var mat = p.Mat;
            var baseTex = MaterialTexture(MatBase(mat),
                "bc", whitePx);
            if (baseTex == null) continue;
            bindings.Clear();
            bindings.Set("verts", vb);
            bindings.Set("base_color", baseTex);
            bindings.Set("light_mvp", lightMvp);
            bindings.Set("model", model);
            bindings.Set("base_color_factor", BaseColorFactor(mat));
            bindings.Set("material", MaterialParams(mat));
            var ib = p.Ib;
            if (ib != null) bindings.Set("indices", ib);
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

        var bindings = new Bindings();
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
            bindings.Clear();
            bindings.Set("verts", vb);
            bindings.Set("base_color", baseTex);
            bindings.Set("metallic_roughness", mrTex);
            bindings.Set("normal_map", nTex);
            bindings.Set("proj", proj);
            bindings.Set("view", view);
            bindings.Set("model", model);
            bindings.Set("light_mvp", lightMvp);
            bindings.Set("base_color_factor", BaseColorFactor(mat));
            bindings.Set("material", MaterialParams(mat));
            bindings.Set("normal_params", NormalParams(mat));
            var ib = p.Ib;
            if (ib != null) bindings.Set("indices", ib);
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
        var vl = viewToLight.ToList();
        var bindings = new Bindings();
        bindings.Set("verts", quad);
        bindings.Set("g_albedo", gAlbedo);
        bindings.Set("g_normal", gNormal);
        bindings.Set("g_position", gPosition);
        bindings.Set("shadow_map", shadowMap);
        bindings.Set("ao_map", aoTex);
        bindings.Set("light0", new Vec4(l0.X, l0.Y, l0.Z, 5.6f));
        bindings.Set("light1", new Vec4(l1.X, l1.Y, l1.Z, 0.7f));
        bindings.Set("params", new Vec4(1.0f, 0.050f, 0.82f, 0.85f));
        bindings.Set("vl0", new Vec4(vl[0], vl[1], vl[2], vl[3]));
        bindings.Set("vl1", new Vec4(vl[4], vl[5], vl[6], vl[7]));
        bindings.Set("vl2", new Vec4(vl[8], vl[9], vl[10], vl[11]));
        bindings.Set("vl3", new Vec4(vl[12], vl[13], vl[14], vl[15]));
        Gfx.Draw(6, bindings, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void Blit(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        var bindings = new Bindings();
        bindings.Set("verts", quad);
        bindings.Set("scene", tex);
        Gfx.Draw(6, bindings, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void BlitFog(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef gPosition)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        var bindings = new Bindings();
        bindings.Set("verts", quad);
        bindings.Set("scene", tex);
        bindings.Set("gpos", gPosition);
        Gfx.Draw(6, bindings, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void BlitCombine(TextureRef targ, ShaderRef shader,
        BufferRef quad, TextureRef baseTex, TextureRef bloom)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        var bindings = new Bindings();
        bindings.Set("verts", quad);
        bindings.Set("scene", baseTex);
        bindings.Set("bloom", bloom);
        Gfx.Draw(6, bindings, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void BlitOutline(TextureRef targ, ShaderRef shader,
        BufferRef quad, TextureRef tex, TextureRef gNormal,
        TextureRef gPosition)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        var bindings = new Bindings();
        bindings.Set("verts", quad);
        bindings.Set("scene", tex);
        bindings.Set("gnormal", gNormal);
        bindings.Set("gpos", gPosition);
        Gfx.Draw(6, bindings, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void BlitDof(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef blurred, TextureRef gPosition)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        var bindings = new Bindings();
        bindings.Set("verts", quad);
        bindings.Set("scene", tex);
        bindings.Set("blurred", blurred);
        bindings.Set("gpos", gPosition);
        Gfx.Draw(6, bindings, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
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
        var bindings = new Bindings();
        bindings.Set("verts", quad);
        bindings.Set("gnormal", gNormal);
        bindings.Set("gpos", gPosition);
        bindings.Set("params", new Vec4(p00, p11, 0.0f, 0.0f));
        Gfx.Draw(6, bindings, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void MotionPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, TextureRef gPosition, Mat4 m)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        var mm = m.ToList();
        var bindings = new Bindings();
        bindings.Set("verts", quad);
        bindings.Set("scene", tex);
        bindings.Set("gpos", gPosition);
        bindings.Set("r0", new Vec4(mm[0], mm[1], mm[2], mm[3]));
        bindings.Set("r1", new Vec4(mm[4], mm[5], mm[6], mm[7]));
        bindings.Set("r2", new Vec4(mm[8], mm[9], mm[10], mm[11]));
        bindings.Set("r3", new Vec4(mm[12], mm[13], mm[14], mm[15]));
        Gfx.Draw(6, bindings, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void ScreenPass(TextureRef targ, ShaderRef shader, BufferRef quad,
        TextureRef tex, int mode)
    {
        Gfx.BeginPass(new PassOpts { Target = targ, ClearColor = Black() });
        var bindings = new Bindings();
        bindings.Set("verts", quad);
        bindings.Set("scene", tex);
        bindings.Set("params", new Vec4(mode, 0.0f, 0.0f, 0.0f));
        Gfx.Draw(6, bindings, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    static void Present(ShaderRef shader, BufferRef quad, TextureRef tex)
    {
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = Black(),
        });
        var bindings = new Bindings();
        bindings.Set("verts", quad);
        bindings.Set("scene", tex);
        Gfx.Draw(6, bindings, new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
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

    // env 値 ([-+]?digits[.digits]) を読む。
    // 読めない文字列は null (呼び出し側は代入をスキップする)。
    static float? ParseNumber(string s)
    {
        var str = new Utf8Bytes(s.Trim());
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
