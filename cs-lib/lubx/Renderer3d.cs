// Renderer3d of the implementation library lubx.
// Design notes:
// - Draw3dOpts / Camera and light / sky / shadow / ssao / bloom / aa / fog /
//   outline are options classes. Optional values are nullable fields + ??; only fog / outline,
//   which have 2 required fields, are received in the constructor. The draw list is
//   Renderer3dDrawCmd (internal).
// - Extra textures for a replacement shader are Set into Bindings with a foreach over Dictionary<string, TextureRef>.
// - Mat4.m (List<float>) is passed directly.
//   bones is the List<float> returned by Bones.pack(),
//   or identityBones() (a dummy resolve lambda) when mesh is null.
// - A null from use_texture / use_buffer / mesh.vb is guarded with an early return / continue
//   (cs-lib convention).
// - viewProj is a public field (only begin() writes it, and users
//   treat it as read-only). litUniforms receives the vp already narrowed in End() as an argument.
// - No deferred rendering.
//   Writing the G-buffer to memory between passes and reading it back is expensive on web and mobile GPUs, and with a single directional light deferred has no benefit.

using System;
using System.Collections.Generic;
using Lub;

namespace Lubx;

/// <summary>Per-draw options for `Renderer3d.draw()`.
/// </summary>
public class Draw3dOpts
{
    /// <summary>Color multiplied with the vertex color (white if omitted).
    /// Even with a &lt; 1 it does not blend automatically.
    /// </summary>
    public Color? Tint;

    /// <summary>`Blends.Alpha` etc.
    /// When set, it is drawn after the opaque group and casts no shadow.
    /// </summary>
    public Blend? Blend;

    /// <summary>For skinned meshes.
    /// The 256 floats from `Bones.pack()`.
    /// </summary>
    public List<float>? Bones;

    /// <summary>Material replacement.
    /// The vertex layout follows the same contract as the default shader, and `Renderer3d.ShaderUniforms` (`Renderer3d.SkinnedShaderUniforms` for skinned meshes) is prepended to the vertex and fragment source.
    /// </summary>
    public ShaderRef? Shader;

    /// <summary>Extra textures for the replacement shader (name → TextureRef).
    /// </summary>
    public Dictionary<string, TextureRef>? Textures;
}

/// <summary>Camera for `Renderer3d.begin()`.
/// Same shape as `Camera3d.vp`.
/// eye / target are required; the rest are optional.
/// </summary>
public class Camera
{
    public Vec3 Eye = new Vec3(0, 0, 0);
    public Vec3 Target = new Vec3(0, 0, 0);
    public Vec3? Up;

    /// <summary>Degrees.
    /// Defaults to 60.
    /// </summary>
    public float? Fov;

    /// <summary>Distance to the near clip plane.
    /// Defaults to 0.1.
    /// </summary>
    public float? Near;

    /// <summary>Distance to the far clip plane.
    /// Defaults to 100.
    /// Anything farther than this is not drawn (increase it when placing a distant background).
    /// </summary>
    public float? Far;
}

/// <summary>Per-draw record of Renderer3d (internal).
/// </summary>
public class Renderer3dDrawCmd
{
    public Mesh3d Mesh;
    public Mat4 Model;
    public Color Tint;
    public Blend? Blend;
    public List<float>? Bones;
    public ShaderRef? Shader;
    public Dictionary<string, TextureRef>? Textures;

    public Renderer3dDrawCmd(Mesh3d mesh, Mat4 model, Color tint,
        Blend? blend, List<float>? bones, ShaderRef? shader,
        Dictionary<string, TextureRef>? textures)
    {
        this.Mesh = mesh;
        this.Model = model;
        this.Tint = tint;
        this.Blend = blend;
        this.Bones = bones;
        this.Shader = shader;
        this.Textures = textures;
    }
}

/// <summary>Directional light.
/// `dir` is the vector toward the light (need not be normalized).
/// </summary>
public class Renderer3dLight
{
    public Vec3 Dir = new Vec3(-0.4f, 1.0f, -0.55f);
    public Color Color = Color.Rgb(1.0f, 0.96f, 0.9f);
    public float Intensity = 1.25f;
}

/// <summary>Hemispheric ambient sky color (top) / ground color (bottom) and intensity.
/// </summary>
public class Renderer3dSky
{
    public Color Top = Color.Rgb(0.42f, 0.48f, 0.58f);
    public Color Bottom = Color.Rgb(0.20f, 0.18f, 0.16f);
    public float Intensity = 0.55f;
}

/// <summary>shadow map.
/// `center`/`extent` are the light's ortho range (world).
/// </summary>
public class Renderer3dShadow
{
    public bool Enabled = true;
    public int Size = 2048;
    public Vec3 Center = new Vec3(0, 0, 0);
    public float Extent = 12.0f;
    /// <summary>Value subtracted from the receiving surface's depth in the shadow depth comparison (depth 0 to 1).
    /// </summary>
    public float Bias = 0.004f;
}

/// <summary>SSAO (half resolution, derived from depth).
/// `radius` is in view space.
/// </summary>
public class Renderer3dSsao
{
    public bool Enabled = true;
    public float Radius = 0.6f;
    public float Strength = 0.85f;
}

/// <summary>bloom.
/// `threshold` is the HDR luminance, `strength` is the blend amount.
/// </summary>
public class Renderer3dBloom
{
    public bool Enabled = true;
    public float Threshold = 1.0f;
    public float Strength = 0.35f;
}

/// <summary>Post AA (FXAA).
/// </summary>
public class Renderer3dAa
{
    public bool Enabled = true;
}

/// <summary>Distance fog (opt in by assigning to `Renderer3d.fog`).
/// `density` is a 1/distance scale.
/// </summary>
public class Renderer3dFog
{
    public Color Color;
    public float Density;

    public Renderer3dFog(Color color, float density)
    {
        this.Color = color;
        this.Density = density;
    }
}

/// <summary>Outline along depth edges (opt in by assigning to `Renderer3d.outline`).
/// `threshold` is the view distance difference.
/// </summary>
public class Renderer3dOutline
{
    public Color Color;
    public float Threshold;

    public Renderer3dOutline(Color color, float threshold)
    {
        this.Color = color;
        this.Threshold = threshold;
    }
}

/// <summary>
/// Built-in forward + HDR renderer.
/// The goal is "throw in a mesh and get a good picture in one go".
///
/// <code>
/// var ren = new Renderer3d("main");
/// // Every frame:
/// ren.begin(new Camera { eye = eye, target = tgt, fov = 38 });
/// ren.draw(mesh, model);
/// ren.draw(charMesh, m2, new Draw3dOpts { bones = packed });
/// ren.End(); // shadow → forward(HDR) → tonemap → swapchain
/// </code>
///
/// On by default: directional light + hemispheric ambient, shadow (PCF 3×3), AgX tonemap.
/// The look is tuned with per-effect fields (`light` / `sky` / `shadow` / `exposure`).
/// The intermediate images declared under key (`<key>/ldr`, `<key>/hdr`, etc.) have their first row at the top of the screen like other textures, and can be drawn as is with SpriteBatch and the like.
///
/// After `End()`, UI and text can be drawn over the swapchain with `Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load })`.
/// </summary>
public class Renderer3d
{
    /// <summary>Declaration of the uniforms that Renderer3d passes, which a replacement shader prepends to its vertex and fragment source (for static meshes).
    /// The shader reads them like `r3d.mvp`.
    /// The passed uniforms and the declaration must match, so a replacement shader cannot have its own uniforms (if needed, copy Renderer3d and extend it).
    /// </summary>
    public static string ShaderUniforms = """

        struct Renderer3dUniforms {
          float4x4 mvp;       // model to clip
          float4x4 model;     // model to world
          float4x4 light_mvp; // world to shadow map clip
          float4 tint;        // Draw3dOpts.Tint (sRGB)
          float4 light_dir;   // direction toward the light (world, normalized)
          float4 light_col;   // light color × intensity
          float4 sky_col;     // upper color of the hemispheric ambient light, w = ambient intensity
          float4 ground_col;  // lower color of the hemispheric ambient light
          float4 cam_pos;     // camera position (world)
          float4 shadow_p;    // x = 1 / shadow map side length, y = bias, z = 1 if shadows are enabled
        };
        ConstantBuffer<Renderer3dUniforms> r3d;

        """;

    /// <summary>Declaration that adds the joint matrices `bones` to `ShaderUniforms` (for skinned meshes).
    /// </summary>
    public static string SkinnedShaderUniforms = """

        struct Renderer3dUniforms {
          float4x4 mvp;
          float4x4 model;
          float4x4 light_mvp;
          float4 tint;
          float4 light_dir;
          float4 light_col;
          float4 sky_col;
          float4 ground_col;
          float4 cam_pos;
          float4 shadow_p;
          float4x4 bones[16]; // the 16 from Bones.Pack
        };
        ConstantBuffer<Renderer3dUniforms> r3d;

        """;

    // --- Embedded shaders (pncm / pncmw vertex layout contract) ------------------- Vertices are fetched from StructuredBuffer `verts` with LUB_VERTEX_ID (vertex pulling).
    // The struct order and padding match the float sequence of Io.interleave_pncm / interleave_pncmw (the buffers built by Mesh3d are read as is).
    private static string pncmVerts = """

        struct V {
          float3 pos;
          float pad0;
          float3 normal;
          float pad1;
          float3 color;
          float pad2;
          float2 mr; // metallic, roughness
          float2 pad3;
        };
        StructuredBuffer<V> verts;

        """;

    private static string pncmwVerts = """

        struct V {
          float3 pos;
          float pad0;
          float3 normal;
          float pad1;
          float3 color;
          float pad2;
          float2 mr; // metallic, roughness
          float2 pad3;
          float4 skin; // j0, w0, j1, w1
        };
        StructuredBuffer<V> verts;

        """;

    private static string litVsCommon = """

        struct Uniforms {
          float4x4 mvp;
          float4x4 model;
          float4x4 light_mvp;
          float4 tint;

        """;

    private static string litVsBody = """

        struct VSOut {
          float3 wn : TEXCOORD0;
          float3 wp : TEXCOORD1;
          float4 lpos : TEXCOORD2;
          float2 mr : TEXCOORD3;
          float4 albedo : COLOR0;
          float4 pos : SV_Position;
        };

        """;

    private static string litStaticVs = litVsCommon
        + """
        };
        ConstantBuffer<Uniforms> u;
        """
        + pncmVerts
        + litVsBody
        + """

        [shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
          V i = verts[vid];
          VSOut o;
          float4 wp4 = mul(u.model, float4(i.pos, 1.0f));
          o.pos = mul(u.mvp, float4(i.pos, 1.0f));
          o.wn = mul(u.model, float4(i.normal, 0.0f)).xyz;
          o.wp = wp4.xyz;
          o.lpos = mul(u.light_mvp, wp4);
          // Vertex color / tint are sRGB authored.
          // Lighting is done in linear, and AgX brings it back to display.
          float3 srgb = i.color * u.tint.rgb;
          o.albedo = float4(pow(srgb, float3(2.2f, 2.2f, 2.2f)), u.tint.a);
          o.mr = i.mr;
          return o;
        }

        """;

    private static string litSkinnedVs = litVsCommon
        + """
          float4x4 bones[16];
        };
        ConstantBuffer<Uniforms> u;
        """
        + pncmwVerts
        + litVsBody
        + """

        [shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
          V i = verts[vid];
          VSOut o;
          int j0 = int(i.skin.x);
          int j1 = int(i.skin.z);
          float4 p4 = float4(i.pos, 1.0f);
          float3 sp =
              (mul(u.bones[j0], p4) * i.skin.y + mul(u.bones[j1], p4) * i.skin.w).xyz;
          float3 sn = mul((float3x3)u.bones[j0], i.normal) * i.skin.y +
                      mul((float3x3)u.bones[j1], i.normal) * i.skin.w;
          float4 wp4 = mul(u.model, float4(sp, 1.0f));
          o.pos = mul(u.mvp, float4(sp, 1.0f));
          o.wn = mul(u.model, float4(sn, 0.0f)).xyz;
          o.wp = wp4.xyz;
          o.lpos = mul(u.light_mvp, wp4);
          float3 srgb = i.color * u.tint.rgb;
          o.albedo = float4(pow(srgb, float3(2.2f, 2.2f, 2.2f)), u.tint.a);
          o.mr = i.mr;
          return o;
        }

        """;

    // The dielectric/metal branch comes from 23_crane_game.
    // Light direction and ambient light are uniforms, and shadow is applied to the directional light component.
    // Output is HDR (not clamped).
    private static string litFs = """

        LUB_TEXTURE2D(shadow_map);
        struct FsU {
          float4 light_dir; // world, toward light (normalized)
          float4 light_col; // rgb * intensity
          float4 sky_col;   // hemispheric ambient (top), w = ambient intensity
          float4 ground_col; // hemispheric ambient (bottom)
          float4 cam_pos;   // world camera (for specular)
          float4 shadow_p;  // x = 1/texsize, y = bias, z = enabled
        };
        ConstantBuffer<FsU> f;
        struct FSIn {
          float3 wn : TEXCOORD0;
          float3 wp : TEXCOORD1;
          float4 lpos : TEXCOORD2;
          float2 mr : TEXCOORD3;
          float4 albedo : COLOR0;
        };

        // Called before any per-pixel divergent branch so that derivatives match those of neighboring pixels.
        float2 shadow_depth_gradient(float4 lpos) {
          float3 p = lpos.xyz / lpos.w;
          p.xy = p.xy * float2(0.5f, -0.5f) + 0.5f;
          float3 dx = ddx(p), dy = ddy(p);
          float det = dx.x * dy.y - dx.y * dy.x;
          if (abs(det) < 1e-15f)
            return float2(0.0f, 0.0f);
          return float2(dx.z * dy.y - dy.z * dx.y,
                        dx.x * dy.z - dy.x * dx.z) / det;
        }

        float shadow_factor(float4 lpos, float2 dz) {
          if (f.shadow_p.z < 0.5f)
            return 1.0f;
          float3 ndc = lpos.xyz / lpos.w;
          float2 uv = ndc.xy * 0.5f + 0.5f;
          uv.y = 1.0f - uv.y; // shadow map stored y-down vs the lookup uv
          if (uv.x < 0.0f || uv.x > 1.0f || uv.y < 0.0f || uv.y > 1.0f || ndc.z < 0.0f ||
              ndc.z > 1.0f)
            return 1.0f;
          float texel = f.shadow_p.x;
          // Align the center of the texel being read with the position of the receiving surface's depth.
          float2 coord = uv / texel - 0.5f;
          float2 base = floor(coord), fracUv = frac(coord);
          float lit = 0.0f;
          // Interpolate the 3x3 PCF according to the position.
          // The 4x4 weights sum to 9.
          for (int y = -1; y <= 2; ++y)
            for (int x = -1; x <= 2; ++x) {
              float2 sampleUv = (base + float2(float(x), float(y)) + 0.5f) * texel;
              sampleUv = clamp(sampleUv, texel * 0.5f, 1.0f - texel * 0.5f);
              float closest = LUB_SAMPLE_LOD(shadow_map, sampleUv).r;
              float receiver = ndc.z + dot(dz, sampleUv - uv);
              float wx = x == -1 ? 1.0f - fracUv.x : (x == 2 ? fracUv.x : 1.0f);
              float wy = y == -1 ? 1.0f - fracUv.y : (y == 2 ? fracUv.y : 1.0f);
              lit += receiver - f.shadow_p.y <= closest ? wx * wy : 0.0f;
            }
          return lit / 9.0f;
        }

        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          float3 n = normalize(i.wn);
          float3 l = f.light_dir.xyz;
          float metal = i.mr.x;
          float rough = i.mr.y;
          float ndl = dot(n, l);
          // Both diffuse and specular apply only on the lit side; the back side is lit by the ambient light.
          float2 shadowGradient = shadow_depth_gradient(i.lpos);
          float sh = ndl > 0.0f ? shadow_factor(i.lpos, shadowGradient) : 0.0f;
          float up = n.y * 0.5f + 0.5f;
          float3 hemi = lerp(f.ground_col.rgb, f.sky_col.rgb, up) * f.sky_col.w;
          float3 v = normalize(f.cam_pos.xyz - i.wp);
          float3 hv = normalize(l + v);

          // Dielectric: Lambert + hemispheric ambient + specular narrowed by roughness
          float diff = saturate(ndl);
          float3 direct = f.light_col.rgb * diff * sh;
          float spec =
              pow(max(dot(n, hv), 0.0f), 32.0f) * (1.0f - rough) * 0.5f * sh;
          float3 dielectric = i.albedo.rgb * (direct + hemi) + f.light_col.rgb * spec;

          // Metal: top-bottom gradient environment + strong specular
          float3 env = lerp(f.ground_col.rgb * 0.8f, f.sky_col.rgb * 1.6f, up);
          float3 metallic = env * lerp(i.albedo.rgb, float3(1.0f, 1.0f, 1.0f), 0.5f);
          metallic +=
              f.light_col.rgb * pow(max(dot(n, hv), 0.0f), 64.0f) * (1.0f - rough) * 1.2f * sh;

          return float4(lerp(dielectric, metallic, metal), i.albedo.a);
        }

        """;

    private static string shadowStaticVs = """

        struct U {
          float4x4 light_mvp;
          float4x4 model;
        };
        ConstantBuffer<U> u;
        """
        + pncmVerts
        + """
        struct VSOut {
          float4 pos : SV_Position;
        };
        [shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
          V i = verts[vid];
          VSOut o;
          o.pos = mul(u.light_mvp, mul(u.model, float4(i.pos, 1.0f)));
          return o;
        }

        """;

    private static string shadowSkinnedVs = """

        struct U {
          float4x4 light_mvp;
          float4x4 model;
          float4x4 bones[16];
        };
        ConstantBuffer<U> u;
        """
        + pncmwVerts
        + """
        struct VSOut {
          float4 pos : SV_Position;
        };
        [shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
          V i = verts[vid];
          VSOut o;
          int j0 = int(i.skin.x);
          int j1 = int(i.skin.z);
          float4 p4 = float4(i.pos, 1.0f);
          float3 sp =
              (mul(u.bones[j0], p4) * i.skin.y + mul(u.bones[j1], p4) * i.skin.w).xyz;
          o.pos = mul(u.light_mvp, mul(u.model, float4(sp, 1.0f)));
          return o;
        }

        """;

    // The depth-only pass has no color attachment, so it outputs nothing.
    private static string shadowFs = """

        [shader("fragment")] void fs_main() {
        }

        """;

    // Quad covering the entire target (pos, uv).
    // clip y = +1 (the target's first row) is uv.y = 0 (the source's first row), so it copies without flipping vertically.
    private static List<float> fullQuad = new List<float>
    {
        -1, -1, 0, 1,
         1, -1, 1, 1,
         1,  1, 1, 0,
        -1, -1, 0, 1,
         1,  1, 1, 0,
        -1,  1, 0, 0,
    };

    // SSAO: reconstructs the view position from depth, and the face normal from depth derivatives.
    // Half resolution.
    // The kernel is a fixed 12-sample spiral (no randomness = deterministic).
    private static string ssaoFs = """

        LUB_TEXTURE2D(depth_tex);
        struct FsU {
          float4 pp;    // m0, m5, A (m10), B (m11)
          float4 ao_p;  // x = radius (view), y = strength, z = 1/w, w = 1/h
        };
        ConstantBuffer<FsU> f;
        struct FSIn {
          float2 uv : TEXCOORD0;
        };

        float3 view_pos(float2 uv) {
          float d = LUB_SAMPLE_LOD(depth_tex, uv).r;
          // Inverse of the LH projection (m10 = A, m11 = B < 0): z = B / (d - A).
          // d - A is always negative.
          float vz = f.pp.w / min(d - f.pp.z, -1e-6f);
          float x = (uv.x * 2.0f - 1.0f) * vz / f.pp.x;
          float y = (1.0f - uv.y * 2.0f) * vz / f.pp.y;
          return float3(x, y, vz);
        }

        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          float3 p = view_pos(i.uv);
          // view_pos is x right, y up, z into the screen.
          // The cross product with the screen's downward derivative makes it face the camera, and occluders in front of the surface are counted.
          float3 n = normalize(cross(ddx(p), ddy(p)));
          // 12-point spiral offsets (screen space) scaled by the view radius
          float rpx = f.ao_p.x / p.z * f.pp.y * 0.5f; // radius to uv scale
          float occ = 0.0f;
          float ang = 2.399963f; // golden angle
          for (int k = 0; k < 12; ++k) {
            float fk = (float(k) + 0.5f) / 12.0f;
            float r = sqrt(fk) * rpx;
            float a = float(k) * ang;
            float2 duv = float2(cos(a) * r, sin(a) * r);
            float3 q = view_pos(i.uv + duv);
            float3 dq = q - p;
            float dist = length(dq);
            float ndotd = dot(n, dq / max(dist, 1e-6f));
            // Count as occlusion only surfaces that protrude in front within the radius
            float range = saturate(1.0f - dist / f.ao_p.x);
            occ += saturate(ndotd - 0.02f) * range;
          }
          float ao = 1.0f - saturate(occ / 12.0f * 2.2f) * f.ao_p.y;
          return float4(ao, ao, ao, 1.0f);
        }

        """;

    // bloom extraction: soft-knee threshold.
    private static string brightFs = """

        LUB_TEXTURE2D(scene);
        struct FsU {
          float4 bl; // x = threshold, y = knee
        };
        ConstantBuffer<FsU> f;
        struct FSIn {
          float2 uv : TEXCOORD0;
        };
        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          float3 c = LUB_SAMPLE_LOD(scene, i.uv).rgb;
          float lum = max(c.r, max(c.g, c.b));
          float knee = f.bl.y;
          float soft = saturate(lum - f.bl.x + knee) ;
          soft = soft * soft / (4.0f * max(knee, 1e-4f));
          float w = max(soft, lum - f.bl.x) / max(lum, 1e-4f);
          return float4(c * saturate(w), 1.0f);
        }

        """;

    // Downsample/upsample (LINEAR sampler + 4-tap tent).
    // up is drawn with ADDITIVE blend.
    private static string blitTentFs = """

        LUB_TEXTURE2D(scene);
        struct FsU {
          float4 st; // x = 1/srcW, y = 1/srcH, z = gain
        };
        ConstantBuffer<FsU> f;
        struct FSIn {
          float2 uv : TEXCOORD0;
        };
        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          float2 t = f.st.xy;
          float3 c = LUB_SAMPLE_LOD(scene, i.uv + float2(-t.x, -t.y)).rgb;
          c += LUB_SAMPLE_LOD(scene, i.uv + float2(t.x, -t.y)).rgb;
          c += LUB_SAMPLE_LOD(scene, i.uv + float2(-t.x, t.y)).rgb;
          c += LUB_SAMPLE_LOD(scene, i.uv + float2(t.x, t.y)).rgb;
          return float4(c * 0.25f * f.st.z, 1.0f);
        }

        """;

    // composite: scene * AO + bloom, fog, outline (both opt-in, HDR space).
    // AO is applied to the whole color after lighting.
    // Applying it only to the ambient light would need a depth-only pass before the forward pass; this is a trade-off to avoid adding that pass.
    private static string compositeFs = """

        LUB_TEXTURE2D(scene);
        LUB_TEXTURE2D(ao_tex);
        LUB_TEXTURE2D(bloom_tex);
        LUB_TEXTURE2D(depth_tex);
        struct FsU {
          float4 pp;      // m0, m5, A, B (view reconstruction)
          float4 en;      // x = ao on, y = bloom strength, z = fog on, w = outline on
          float4 fog_col; // rgb, w = density
          float4 ol;      // rgb = outline color, w = depth threshold (view)
          float4 px;      // x = 1/w, y = 1/h
        };
        ConstantBuffer<FsU> f;
        struct FSIn {
          float2 uv : TEXCOORD0;
        };

        float view_z(float2 uv) {
          float d = LUB_SAMPLE_LOD(depth_tex, uv).r;
          return f.pp.w / min(d - f.pp.z, -1e-6f);
        }

        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          float3 c = LUB_SAMPLE_LOD(scene, i.uv).rgb;
          if (f.en.x > 0.5f)
            c *= LUB_SAMPLE_LOD(ao_tex, i.uv).r;
          c += LUB_SAMPLE_LOD(bloom_tex, i.uv).rgb * f.en.y;
          float vz = view_z(i.uv);
          if (f.en.w > 0.5f) {
            // depth edge detection (4 neighbors)
            float2 t = f.px.xy;
            float zn = view_z(i.uv + float2(0.0f, -t.y));
            float zs = view_z(i.uv + float2(0.0f, t.y));
            float ze = view_z(i.uv + float2(t.x, 0.0f));
            float zw = view_z(i.uv + float2(-t.x, 0.0f));
            float edge = max(max(abs(zn - vz), abs(zs - vz)), max(abs(ze - vz), abs(zw - vz)));
            float o = saturate((edge - f.ol.w) / f.ol.w);
            c = lerp(c, f.ol.rgb, saturate(o) * 0.85f);
          }
          if (f.en.z > 0.5f) {
            float fogf = 1.0f - exp2(-vz * f.fog_col.w);
            c = lerp(c, f.fog_col.rgb, saturate(fogf));
          }
          return float4(c, 1.0f);
        }

        """;

    // FXAA (simplified console-style version).
    // Applied to LDR.
    private static string fxaaFs = """

        LUB_TEXTURE2D(scene);
        struct FsU {
          float4 px; // x = 1/w, y = 1/h
        };
        ConstantBuffer<FsU> f;
        struct FSIn {
          float2 uv : TEXCOORD0;
        };
        float luma(float3 c) { return dot(c, float3(0.299f, 0.587f, 0.114f)); }
        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          float2 t = f.px.xy;
          float3 cM = LUB_SAMPLE_LOD(scene, i.uv).rgb;
          float lM = luma(cM);
          float lNW = luma(LUB_SAMPLE_LOD(scene, i.uv + float2(-t.x, -t.y)).rgb);
          float lNE = luma(LUB_SAMPLE_LOD(scene, i.uv + float2(t.x, -t.y)).rgb);
          float lSW = luma(LUB_SAMPLE_LOD(scene, i.uv + float2(-t.x, t.y)).rgb);
          float lSE = luma(LUB_SAMPLE_LOD(scene, i.uv + float2(t.x, t.y)).rgb);
          float lMin = min(lM, min(min(lNW, lNE), min(lSW, lSE)));
          float lMax = max(lM, max(max(lNW, lNE), max(lSW, lSE)));
          if (lMax - lMin < max(0.0312f, lMax * 0.125f))
            return float4(cM, 1.0f);
          float2 dir = float2(-((lNW + lNE) - (lSW + lSE)), (lNW + lSW) - (lNE + lSE));
          float dirReduce = max((lNW + lNE + lSW + lSE) * 0.03125f, 0.0078125f);
          float rcpMin = 1.0f / (min(abs(dir.x), abs(dir.y)) + dirReduce);
          dir = clamp(dir * rcpMin, float2(-8.0f, -8.0f), float2(8.0f, 8.0f)) * t;
          float3 a = 0.5f * (LUB_SAMPLE_LOD(scene, i.uv + dir * (1.0f / 3.0f - 0.5f)).rgb +
                            LUB_SAMPLE_LOD(scene, i.uv + dir * (2.0f / 3.0f - 0.5f)).rgb);
          float3 b = a * 0.5f + 0.25f * (LUB_SAMPLE_LOD(scene, i.uv + dir * -0.5f).rgb +
                                       LUB_SAMPLE_LOD(scene, i.uv + dir * 0.5f).rgb);
          float lB = luma(b);
          return float4((lB < lMin || lB > lMax) ? a : b, 1.0f);
        }

        """;

    // Plain blit (present when FXAA is off).
    private static string presentFs = """

        LUB_TEXTURE2D(scene);
        struct FSIn {
          float2 uv : TEXCOORD0;
        };
        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          return float4(LUB_SAMPLE_LOD(scene, i.uv).rgb, 1.0f);
        }

        """;

    // For presenting to the screen.
    // Places the quad in the range dst inside the target (clip left, bottom, right, top).
    private static string presentVs = """

        struct Q {
          float2 pos;
          float2 uv;
        };
        StructuredBuffer<Q> verts;
        struct PresentU {
          float4 dst;
        };
        ConstantBuffer<PresentU> pr;
        struct VSOut {
          float2 uv : TEXCOORD0;
          float4 pos : SV_Position;
        };
        [shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
          Q i = verts[vid];
          VSOut o;
          o.pos = float4(lerp(pr.dst.xy, pr.dst.zw, i.pos * 0.5f + 0.5f), 0.0f, 1.0f);
          o.uv = i.uv;
          return o;
        }

        """;

    private static string quadVs = """

        struct Q {
          float2 pos;
          float2 uv;
        };
        StructuredBuffer<Q> verts;
        struct VSOut {
          float2 uv : TEXCOORD0;
          float4 pos : SV_Position;
        };
        [shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
          Q i = verts[vid];
          VSOut o;
          o.pos = float4(i.pos, 0.0f, 1.0f);
          o.uv = i.uv;
          return o;
        }

        """;

    // AgX (minimal fit).
    // HDR → display.
    // exposure is in stops (2^n).
    private static string tonemapFs = """

        LUB_TEXTURE2D(scene);
        struct FsU {
          float4 grade; // x = exposure (stops), y = vignette, z = dither, w = screen height
        };
        ConstantBuffer<FsU> f;
        struct FSIn {
          float2 uv : TEXCOORD0;
        };

        float3 agx_contrast(float3 x) {
          float3 x2 = x * x;
          float3 x4 = x2 * x2;
          return 15.5f * x4 * x2 - 40.14f * x4 * x + 31.96f * x4 - 6.868f * x2 * x +
                 0.4298f * x2 + 0.1191f * x - 0.00232f;
        }

        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          float3 c = LUB_SAMPLE_LOD(scene, i.uv).rgb;
          c *= exp2(f.grade.x);
          // AgX inset matrix
          float3 v = float3(0.842479f * c.r + 0.0784336f * c.g + 0.0792237f * c.b,
                            0.0423282f * c.r + 0.878468f * c.g + 0.0791661f * c.b,
                            0.0423756f * c.r + 0.0784336f * c.g + 0.879142f * c.b);
          // log2 encode
          float min_ev = -12.47393f;
          float max_ev = 4.026069f;
          v = clamp(log2(max(v, 1e-10f)), min_ev, max_ev);
          v = (v - min_ev) / (max_ev - min_ev);
          v = agx_contrast(v);
          // outset matrix
          float3 o = float3(1.19688f * v.r - 0.0980209f * v.g - 0.0990297f * v.b,
                            -0.0528968f * v.r + 1.15190f * v.g - 0.0989612f * v.b,
                            -0.0529716f * v.r - 0.0980434f * v.g + 1.15107f * v.b);
          o = saturate(o);
          // punchy look: slight tightening + saturation restore (AgX is dull on its own)
          o = pow(o, float3(1.08f, 1.08f, 1.08f));
          float lum = dot(o, float3(0.2126f, 0.7152f, 0.0722f));
          o = lum + (o - lum) * 1.28f;
          // vignette (grade.y = strength)
          float2 d2 = i.uv - 0.5f;
          o *= 1.0f - dot(d2, d2) * 2.0f * f.grade.y;
          // triangular dither (on with grade.z = 1).
          // It is a coordinate hash, so it is deterministic.
          float h = frac(sin(dot(i.uv * f.grade.w, float2(12.9898f, 78.233f))) * 43758.5453f);
          o += (h - 0.5f) * (2.0f / 255.0f) * f.grade.z;
          return float4(saturate(o), 1.0f);
        }

        """;

    // --- Public options -------------------------------------------------------

    /// <summary>Directional light.
    /// `dir` is the vector toward the light (need not be normalized).
    /// </summary>
    public Renderer3dLight Light = new Renderer3dLight();

    /// <summary>Hemispheric ambient sky color (top) / ground color (bottom) and intensity.
    /// </summary>
    public Renderer3dSky Sky = new Renderer3dSky();

    /// <summary>shadow map.
    /// `center`/`extent` are the light's ortho range (world).
    /// </summary>
    public Renderer3dShadow Shadow = new Renderer3dShadow();

    /// <summary>Exposure (stops).
    /// +1 is twice as bright.
    /// </summary>
    public float Exposure = 0.0f;

    /// <summary>HDR clear color (background).
    /// </summary>
    public Color Background = Color.Rgb(0.09f, 0.12f, 0.15f);

    /// <summary>SSAO (half resolution, derived from depth).
    /// `radius` is in view space.
    /// </summary>
    public Renderer3dSsao Ssao = new Renderer3dSsao();

    /// <summary>bloom.
    /// `threshold` is the HDR luminance, `strength` is the blend amount.
    /// </summary>
    public Renderer3dBloom Bloom = new Renderer3dBloom();

    /// <summary>Post AA (FXAA).
    /// </summary>
    public Renderer3dAa Aa = new Renderer3dAa();

    /// <summary>Triangular dither against 8-bit banding.
    /// </summary>
    public bool Dither = true;

    /// <summary>Vignetting 0..1 (0 = off).
    /// </summary>
    public float Vignette = 0.0f;

    /// <summary>Distance fog (opt-in).
    /// `density` is a 1/distance scale.
    /// </summary>
    public Renderer3dFog? Fog = null;

    /// <summary>Outline along depth edges (opt-in).
    /// `threshold` is the view distance difference.
    /// </summary>
    public Renderer3dOutline? Outline = null;

    /// <summary>For inspecting intermediate buffers: outputs "ao" / "bloom" / "hdr" directly to the swapchain.
    /// </summary>
    public string? DebugView = null;

    /// <summary>Range to draw in on the screen (same px as `Gfx.Size`, top-left origin).
    /// If null, it draws to the whole screen and `End()` clears the screen first.
    /// When set, `End()` draws only inside the range without clearing the screen, so draw outside the range yourself before or after `End()` (to clear it, open a pass before).
    /// `Begin()` reads it; the camera aspect ratio becomes the ratio of the range, and the intermediate images (`<key>/ldr` etc.) become the size of the range.
    /// The clip of `ViewProj` is also in coordinates inside the range, so pass the range's width and height to `Camera3d.Project` and add the range's top-left to the result.
    /// </summary>
    public Rect? Viewport = null;

    /// <summary>world → clip.
    /// For projecting to screen coordinates (HUD following etc.).
    /// Set by begin().
    /// Read-only for users.
    /// </summary>
    public Mat4? ViewProj = null;

    private string key;
    private List<Renderer3dDrawCmd> draws = new List<Renderer3dDrawCmd>();
    private Mat4? view = null;
    private Mat4? proj = null;
    private Mat4? vp = null;
    private Vec3 eye = new Vec3(0, 0, 0);
    private BufferRef? quadBuf = null;
    // The draw range (screen px) decided by Begin(), and the screen size
    private int areaX = 0;
    private int areaY = 0;
    private int areaW = 0;
    private int areaH = 0;
    private int viewportW = 0;
    private int viewportH = 0;

    public Renderer3d(string key)
    {
        this.key = key;
    }

    /// <summary>Phys3d pose (x,y,z,qx,qy,qz,qw) → model matrix.
    /// </summary>
    public static Mat4 PoseMat(Pose3d pose)
    {
        // The box3d quaternion and Quat.ToMat4 are the same active rotation.
        // Inserting a conjugate or transpose here makes physics and rendering diverge (tests/cs/TestRotationConvention.cs)
        return Mat4.Translate(new Vec3(pose.X, pose.Y, pose.Z))
            * new Quat(pose.Qx, pose.Qy, pose.Qz, pose.Qw).ToMat4();
    }

    /// <summary>Frame start.
    /// Fixes the camera and empties the draw list.
    /// </summary>
    public void Begin(Camera cam)
    {
        var up = cam.Up ?? new Vec3(0, 1, 0);
        var fov = cam.Fov ?? 60.0f;
        var near = cam.Near ?? 0.1f;
        var far = cam.Far ?? 100.0f;
        Gfx.Size(out var w, out var h);
        viewportW = w;
        viewportH = h;
        if (Viewport != null)
        {
            var area = Viewport.Value;
            // Trim the part that sticks out of the screen
            areaX = Math.Min(Math.Max(area.X, 0), w);
            areaY = Math.Min(Math.Max(area.Y, 0), h);
            areaW = Math.Max(Math.Min(area.X + area.W, w) - areaX, 0);
            areaH = Math.Max(Math.Min(area.Y + area.H, h) - areaY, 0);
        }
        else
        {
            areaX = 0;
            areaY = 0;
            areaW = w;
            areaH = h;
        }
        var p = Mat4.PerspectiveLh(fov, (float)Math.Max(areaW, 1) / Math.Max(areaH, 1), near, far);
        var v = Mat4.LookAtLh(cam.Eye, cam.Target, up);
        view = v;
        ViewProj = p * v;
        proj = p;
        vp = p * v;
        eye = cam.Eye;
        // Empty draws.
        draws = new List<Renderer3dDrawCmd>();
    }

    /// <summary>Records a draw (executed in `End()`).
    /// </summary>
    public void Draw(Mesh3d? mesh, Mat4 model, Draw3dOpts? opts = null)
    {
        if (mesh == null || !mesh.Ready())
            return;
        var tint = Color.Rgb(1.0f, 1.0f, 1.0f, 1.0f);
        Blend? blend = null;
        List<float>? bones = null;
        ShaderRef? shader = null;
        Dictionary<string, TextureRef>? textures = null;
        if (opts != null)
        {
            var t = opts.Tint;
            if (t != null)
                tint = t.Value;
            blend = opts.Blend;
            bones = opts.Bones;
            shader = opts.Shader;
            textures = opts.Textures;
        }
        draws.Add(new Renderer3dDrawCmd(mesh, model, tint, blend, bones,
            shader, textures));
    }

    private Mat4 LightMvp()
    {
        var len = (float)Math.Sqrt(Light.Dir.X * Light.Dir.X
            + Light.Dir.Y * Light.Dir.Y + Light.Dir.Z * Light.Dir.Z);
        var inv = len > 1e-6f ? 1.0f / len : 1.0f;
        var dist = Shadow.Extent * 1.6f;
        var leye = new Vec3(Shadow.Center.X + Light.Dir.X * inv * dist,
            Shadow.Center.Y + Light.Dir.Y * inv * dist,
            Shadow.Center.Z + Light.Dir.Z * inv * dist);
        // up fallback when dir points straight up
        var up = Math.Abs(Light.Dir.Y) * inv > 0.99f
            ? new Vec3(0, 0, 1)
            : new Vec3(0, 1, 0);
        var lview = Mat4.LookAtLh(leye, Shadow.Center, up);
        return Mat4.OrthoLh(Shadow.Extent * 2.0f, Shadow.Extent * 2.0f, 0.1f,
            dist * 2.0f) * lview;
    }

    // If mesh is null, resolve is not called, but a dummy is passed to keep the Bones.pack contract (resolve is non-null).
    private static List<float> IdentityBones()
    {
        return Bones.Pack(null, (name, px, py, pz) => null);
    }

    private void ShadowPass(Mat4 lmvp, ShaderRef shStatic, ShaderRef shSkinned,
        TextureRef shadowMap)
    {
        Gfx.BeginPass(new PassOpts { DepthTarget = shadowMap, ClearDepth = 1.0f });
        var bindings = passBindings;
        foreach (var d in draws)
        {
            if (d.Blend != null)
                continue; // translucent objects do not cast shadows
            var vb = d.Mesh.Vb;
            var ib = d.Mesh.Ib;
            if (vb == null || ib == null)
                continue;
            bindings.Clear();
            bindings.Set("verts", vb);
            bindings.Set("indices", ib);
            bindings.Set("light_mvp", lmvp);
            bindings.Set("model", d.Model);
            if (d.Mesh.Skinned)
                bindings.Set("bones", d.Bones ?? IdentityBones());
            Gfx.Draw(d.Mesh.IndexCount, bindings, new DrawOpts
            {
                Shader = d.Mesh.Skinned ? shSkinned : shStatic,
                Depth = true,
                DepthWrite = true,
                Cull = Gfx.Cull.None,
            });
        }
        Gfx.EndPass();
    }

    private void SetLitUniforms(Bindings bindings, Renderer3dDrawCmd d,
        Mat4 vp, Mat4 lmvp, float texel)
    {
        bindings.Set("mvp", vp * d.Model);
        bindings.Set("model", d.Model);
        bindings.Set("light_mvp", lmvp);
        // The binding reads the contents as of the draw, so per-draw values are passed by rewriting the same List
        tintTable[0] = d.Tint.R;
        tintTable[1] = d.Tint.G;
        tintTable[2] = d.Tint.B;
        tintTable[3] = d.Tint.A;
        bindings.Set("tint", tintTable);
        bindings.Set("light_dir", LightDirTable());
        bindings.Set("light_col", new Vec4(Light.Color.R * Light.Intensity,
            Light.Color.G * Light.Intensity,
            Light.Color.B * Light.Intensity, 0.0f));
        bindings.Set("sky_col", new Vec4(Sky.Top.R, Sky.Top.G, Sky.Top.B, Sky.Intensity));
        bindings.Set("ground_col", new Vec4(Sky.Bottom.R, Sky.Bottom.G, Sky.Bottom.B, 0.0f));
        bindings.Set("cam_pos", new Vec4(eye.X, eye.Y, eye.Z, 0.0f));
        bindings.Set("shadow_p", new Vec4(texel, Shadow.Bias, Shadow.Enabled ? 1.0f : 0.0f, 0.0f));
        if (d.Mesh.Skinned)
            bindings.Set("bones", d.Bones ?? IdentityBones());
    }

    private readonly List<float> tintTable = new List<float> { 1.0f, 1.0f, 1.0f, 1.0f };
    private readonly List<float> lightDirTable = new List<float> { 0.0f, 0.0f, 0.0f, 0.0f };

    private List<float> LightDirTable()
    {
        var len = (float)Math.Sqrt(Light.Dir.X * Light.Dir.X
            + Light.Dir.Y * Light.Dir.Y + Light.Dir.Z * Light.Dir.Z);
        var inv = len > 1e-6f ? 1.0f / len : 1.0f;
        lightDirTable[0] = Light.Dir.X * inv;
        lightDirTable[1] = Light.Dir.Y * inv;
        lightDirTable[2] = Light.Dir.Z * inv;
        return lightDirTable;
    }

    // Draws one pass over the entire target.
    private void Blit(TextureRef target, ShaderRef shader,
        Bindings bindings, Gfx.LoadAction? load = null,
        Blend? blend = null)
    {
        var fq = quadBuf;
        if (fq == null)
            return;
        var opts = new PassOpts { Target = target };
        if (load != null)
            opts.Load = load;
        Gfx.BeginPass(opts);
        bindings.Set("verts", fq);
        Gfx.Draw(6, bindings, new DrawOpts
        {
            Shader = shader,
            Depth = false,
            Cull = Gfx.Cull.None,
            Blend = blend,
        });
        Gfx.EndPass();
    }

    // Cleared and reused per draw and per pass.
    private readonly Bindings passBindings = new Bindings();

    /// <summary>Executes the recorded draw list through to the swapchain and empties the draw list.
    /// </summary>
    public void End()
    {
        Render();
        // Drawn commands are not carried over to the next frame (if they were, the saved state would keep them alive)
        draws = new List<Renderer3dDrawCmd>();
    }

    private void Render()
    {
        if (this.vp == null || this.proj == null)
            return;
        var vp = this.vp.Value;
        var proj = this.proj.Value;
        // The intermediate images are the size of the draw range
        int w = areaW;
        int h = areaH;
        if (w < 1 || h < 1)
            return;
        var rtVer = w * 65536 + h;

        var litStatic = Gfx.UseShader(key + "/lit_s", litStaticVs, litFs, 1);
        var litSkinned = Gfx.UseShader(key + "/lit_k", litSkinnedVs, litFs, 1);
        var shStatic = Gfx.UseShader(key + "/sh_s", shadowStaticVs, shadowFs, 1);
        var shSkinned = Gfx.UseShader(key + "/sh_k", shadowSkinnedVs, shadowFs, 1);
        var tonemap = Gfx.UseShader(key + "/tm", quadVs, tonemapFs, 1);
        var ssaoSh = Gfx.UseShader(key + "/ssao", quadVs, ssaoFs, 1);
        var brightSh = Gfx.UseShader(key + "/br", quadVs, brightFs, 1);
        var tentSh = Gfx.UseShader(key + "/tent", quadVs, blitTentFs, 1);
        var compSh = Gfx.UseShader(key + "/comp", quadVs, compositeFs, 1);
        var fxaaSh = Gfx.UseShader(key + "/fxaa", presentVs, fxaaFs, 2);
        var presentSh = Gfx.UseShader(key + "/pr", presentVs, presentFs, 2);
        if (litStatic == null || litSkinned == null || shStatic == null
            || shSkinned == null || tonemap == null || ssaoSh == null
            || brightSh == null || tentSh == null || compSh == null
            || fxaaSh == null || presentSh == null)
            return;

        var hdr = Gfx.UseTexture(key + "/hdr", w, h, Gfx.PixelFormat.Rgba16f, null, rtVer,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp });
        var depth = Gfx.UseTexture(key + "/depth", w, h, Gfx.PixelFormat.Depth32f, null,
            rtVer, new TextureOpts { Target = true, Wrap = Gfx.Wrap.Clamp });
        var shadowMap = Gfx.UseTexture(key + "/sm", Shadow.Size, Shadow.Size,
            Gfx.PixelFormat.Depth32f, null, Shadow.Size,
            new TextureOpts { Target = true, Wrap = Gfx.Wrap.Clamp });
        var quad = Gfx.UseBuffer(key + "/quad", Gfx.BufferType.Storage, fullQuad, 2);
        quadBuf = quad;
        if (hdr == null || depth == null || shadowMap == null || quad == null)
            return;

        var lmvp = LightMvp();
        if (Shadow.Enabled)
            ShadowPass(lmvp, shStatic, shSkinned, shadowMap);
        var texel = 1.0f / Shadow.Size;

        // forward pass (HDR)
        Gfx.BeginPass(new PassOpts
        {
            Target = hdr,
            DepthTarget = depth,
            // The background is also sRGB authored → placed in HDR as linear
            ClearColor = new float[]
            {
                (float)Math.Pow(Background.R, 2.2f),
                (float)Math.Pow(Background.G, 2.2f),
                (float)Math.Pow(Background.B, 2.2f),
                1.0f,
            },
            ClearDepth = 1.0f,
        });
        // opaque → blend order
        for (int phase = 0; phase < 2; phase++)
        {
            foreach (var d in draws)
            {
                bool isBlend = d.Blend != null;
                if ((phase == 0) == isBlend)
                    continue;
                var vb = d.Mesh.Vb;
                var ib = d.Mesh.Ib;
                if (vb == null || ib == null)
                    continue;
                var shader = d.Shader
                    ?? (d.Mesh.Skinned ? litSkinned : litStatic);
                var bindings = passBindings;
                bindings.Clear();
                bindings.Set("verts", vb);
                bindings.Set("indices", ib);
                bindings.Set("shadow_map", shadowMap);
                SetLitUniforms(bindings, d, vp, lmvp, texel);
                if (d.Textures != null)
                {
                    foreach (var kv in d.Textures)
                        bindings.Set(kv.Key, kv.Value);
                }
                Gfx.Draw(d.Mesh.IndexCount, bindings, new DrawOpts
                {
                    Shader = shader,
                    Depth = true,
                    DepthWrite = !isBlend,
                    Cull = Gfx.Cull.None,
                    Blend = d.Blend,
                });
            }
        }
        Gfx.EndPass();

        var projP = new List<float>
            { proj.M00, proj.M11, proj.M22, proj.M23 };

        // SSAO (half resolution)
        TextureRef? aoTex = null;
        if (Ssao.Enabled)
        {
            int aw = w / 2;
            int ah = h / 2;
            aoTex = Gfx.UseTexture(key + "/ao", aw, ah, Gfx.PixelFormat.R8, null, rtVer,
                new TextureOpts { Target = true, Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp });
            if (aoTex != null)
            {
                passBindings.Clear();
                passBindings.Set("depth_tex", depth);
                passBindings.Set("pp", projP);
                passBindings.Set("ao_p", new Vec4(Ssao.Radius, Ssao.Strength, 1.0f / aw, 1.0f / ah));
                Blit(aoTex, ssaoSh, passBindings);
            }
        }

        // bloom: bright → 3 downsample levels → back up in reverse order with ADDITIVE
        TextureRef? bloomTex = null;
        if (Bloom.Enabled)
        {
            int levels = 4;
            var texs = new List<TextureRef>();
            var ws = new List<int>();
            var hs = new List<int>();
            int bw = w;
            int bh = h;
            for (int li = 0; li < levels; li++)
            {
                bw = bw / 2;
                bh = bh / 2;
                if (bw < 8 || bh < 8)
                    break;
                var t = Gfx.UseTexture(key + "/bl" + li, bw, bh, Gfx.PixelFormat.Rgba16f,
                    null, rtVer,
                    new TextureOpts { Target = true, Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp });
                if (t == null)
                    break;
                ws.Add(bw);
                hs.Add(bh);
                texs.Add(t);
            }
            if (texs.Count > 0)
            {
                passBindings.Clear();
                passBindings.Set("scene", hdr);
                passBindings.Set("bl", new Vec4(Bloom.Threshold, 0.5f, 0.0f, 0.0f));
                Blit(texs[0], brightSh, passBindings);
                for (int li = 1; li < texs.Count; li++)
                {
                    passBindings.Clear();
                    passBindings.Set("scene", texs[li - 1]);
                    passBindings.Set("st", new Vec4(1.0f / ws[li - 1], 1.0f / hs[li - 1], 1.0f, 0.0f));
                    Blit(texs[li], tentSh, passBindings);
                }
                int j = texs.Count - 1;
                while (j > 0)
                {
                    passBindings.Clear();
                    passBindings.Set("scene", texs[j]);
                    passBindings.Set("st", new Vec4(1.0f / ws[j], 1.0f / hs[j], 0.7f, 0.0f));
                    Blit(texs[j - 1], tentSh, passBindings, Gfx.LoadAction.Load, Blends.Additive);
                    j--;
                }
                bloomTex = texs[0];
            }
        }

        // composite (AO multiply + bloom add + fog / outline)
        var post = Gfx.UseTexture(key + "/post", w, h, Gfx.PixelFormat.Rgba16f, null,
            rtVer, new TextureOpts { Target = true, Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp });
        if (post == null)
            return;
        var fog = this.Fog;
        var outline = this.Outline;
        bool fogOn = fog != null;
        bool olOn = outline != null;
        passBindings.Clear();
        passBindings.Set("scene", hdr);
        passBindings.Set("ao_tex", aoTex ?? hdr);
        passBindings.Set("bloom_tex", bloomTex ?? hdr);
        passBindings.Set("depth_tex", depth);
        passBindings.Set("pp", projP);
        passBindings.Set("en", new Vec4(aoTex != null ? 1.0f : 0.0f, bloomTex != null ? Bloom.Strength : 0.0f, fogOn ? 1.0f : 0.0f, olOn ? 1.0f : 0.0f));
        passBindings.Set("fog_col", fog != null
                    ? new List<float>
                    {
                        (float)Math.Pow(fog.Color.R, 2.2f),
                        (float)Math.Pow(fog.Color.G, 2.2f),
                        (float)Math.Pow(fog.Color.B, 2.2f),
                        fog.Density,
                    }
                    : new List<float> { 0.0f, 0.0f, 0.0f, 0.0f });
        passBindings.Set("ol", outline != null
                    ? new List<float>
                    {
                        (float)Math.Pow(outline.Color.R, 2.2f),
                        (float)Math.Pow(outline.Color.G, 2.2f),
                        (float)Math.Pow(outline.Color.B, 2.2f),
                        outline.Threshold,
                    }
                    : new List<float> { 0.0f, 0.0f, 0.0f, 1.0f });
        passBindings.Set("px", new Vec4(1.0f / w, 1.0f / h, 0.0f, 0.0f));
        Blit(post, compSh, passBindings);

        // tonemap (+vignette +dither) → LDR
        var ldr = Gfx.UseTexture(key + "/ldr", w, h, Gfx.PixelFormat.Rgba8, null, rtVer,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp });
        if (ldr == null)
            return;
        passBindings.Clear();
        passBindings.Set("scene", post);
        passBindings.Set("grade", new Vec4(Exposure, Vignette, Dither ? 1.0f : 0.0f, h));
        Blit(ldr, tonemap, passBindings);

        // FXAA (or passthrough) → the draw range of the swapchain
        var dst = new Vec4((float)areaX / viewportW * 2.0f - 1.0f,
            1.0f - (float)(areaY + h) / viewportH * 2.0f,
            (float)(areaX + w) / viewportW * 2.0f - 1.0f,
            1.0f - (float)areaY / viewportH * 2.0f);
        var present = new PassOpts { Target = Gfx.MainTex };
        if (Viewport != null)
            present.Load = Gfx.LoadAction.Load;
        if (DebugView != null)
        {
            TextureRef? dbg = DebugView switch
            {
                "ao" => aoTex,
                "bloom" => bloomTex,
                "hdr" => hdr,
                _ => null,
            };
            if (dbg != null)
            {
                Gfx.BeginPass(present);
                var bindings = new Bindings();
                bindings.Set("verts", quad);
                bindings.Set("dst", dst);
                bindings.Set("scene", dbg);
                Gfx.Draw(6, bindings,
                    new DrawOpts { Shader = presentSh, Depth = false, Cull = Gfx.Cull.None });
                Gfx.EndPass();
                return;
            }
        }
        Gfx.BeginPass(present);
        if (Aa.Enabled)
        {
            var bindings = new Bindings();
            bindings.Set("verts", quad);
            bindings.Set("dst", dst);
            bindings.Set("scene", ldr);
            bindings.Set("px", new Vec4(1.0f / w, 1.0f / h, 0.0f, 0.0f));
            Gfx.Draw(6, bindings, new DrawOpts { Shader = fxaaSh, Depth = false, Cull = Gfx.Cull.None });
        }
        else
        {
            var bindings = new Bindings();
            bindings.Set("verts", quad);
            bindings.Set("dst", dst);
            bindings.Set("scene", ldr);
            Gfx.Draw(6, bindings,
                new DrawOpts { Shader = presentSh, Depth = false, Cull = Gfx.Cull.None });
        }
        Gfx.EndPass();
    }
}
