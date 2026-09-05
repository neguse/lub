// 実装ライブラリ lubx の Renderer3d。
// 設計メモ:
// - Draw3dOpts / Camera と light / sky / shadow / ssao / bloom / aa / fog /
//   outline は options class。optional は nullable フィールド + ??、必須 2
//   フィールドの fog / outline だけはコンストラクタで受ける。draw 列は
//   Renderer3dDrawCmd (内部用)。
// - uniform / texture の動的マージは Dictionary<string, object> /
//   Dictionary<string, TextureRef> の foreach で bindings dict へ代入する
//   (tcs の Dictionary は素の Lua table なので wire format はそのまま)。
// - Mat4.m (List<float>) は直渡し。bones は Bones.pack() の返す List<float>
//   で、mesh が null のときは identityBones() (ダミー resolve の lambda)。
// - sz.w >> 1 等の bit shift は tcs 未対応なので Math.Floor(x / 2.0)。
// - use_texture / use_buffer / mesh.vb の null は早期 return / continue で
//   ガードする (cs-lib 慣例)。
// - viewProj / viewMat は public フィールド (書くのは begin() だけ、利用側は
//   読み取り専用扱い)。litUniforms は End() で narrow 済みの vp を引数で受ける。
// end は Lua キーワードで、tcs が宣言をそのまま `function Renderer3d:end` と
// emit して不正 Lua になるため End にしている (MeshText の Char と同じ扱い)。

using System;
using System.Collections.Generic;
using static Lub;

/// <summary>`Renderer3d.draw()` の per-draw オプション。</summary>
public class Draw3dOpts
{
    /// <summary>頂点色に乗じる色 (省略時白)。a &lt; 1 でも自動では blend に
    /// ならない。</summary>
    public Color? Tint;

    /// <summary>`Gfx.ALPHA` 等。指定すると opaque 群の後に描かれ、影を
    /// 落とさない。</summary>
    public Gfx.Blend? Blend;

    /// <summary>skinned メッシュ用。`Bones.pack()` の 128 float。</summary>
    public List<float>? Bones;

    /// <summary>material 差し替え。頂点レイアウトと uniform 名は既定 shader
    /// と同じ契約 (必要な uniform 名だけ宣言すればよい)。</summary>
    public ShaderRef? Shader;

    /// <summary>差し替え shader 用の追加テクスチャ (名前 → TextureRef)。</summary>
    public Dictionary<string, TextureRef>? Textures;

    /// <summary>差し替え shader 用の追加 uniform (名前 → List&lt;float&gt;)。
    /// 既定名と衝突したら上書き。</summary>
    public Dictionary<string, object>? Uniforms;
}

/// <summary>`Renderer3d.begin()` のカメラ。`Camera3d.vp` と同じ形。
/// eye / target は必須、他は省略可。</summary>
public class Camera
{
    public Vec3 Eye = new Vec3(0, 0, 0);
    public Vec3 Target = new Vec3(0, 0, 0);
    public Vec3? Up;

    /// <summary>度。省略時 60。</summary>
    public float? Fov;

    public float? Near;
    public float? Far;
}

/// <summary>Renderer3d の per-draw 記録 (内部用)。</summary>
public class Renderer3dDrawCmd
{
    public Mesh3d Mesh;
    public Mat4 Model;
    public List<float> Tint;
    public Gfx.Blend Blend;
    public List<float>? Bones;
    public ShaderRef? Shader;
    public Dictionary<string, TextureRef>? Textures;
    public Dictionary<string, object>? Uniforms;

    public Renderer3dDrawCmd(Mesh3d mesh, Mat4 model, List<float> tint,
        Gfx.Blend blend, List<float>? bones, ShaderRef? shader,
        Dictionary<string, TextureRef>? textures,
        Dictionary<string, object>? uniforms)
    {
        this.Mesh = mesh;
        this.Model = model;
        this.Tint = tint;
        this.Blend = blend;
        this.Bones = bones;
        this.Shader = shader;
        this.Textures = textures;
        this.Uniforms = uniforms;
    }
}

/// <summary>平行光源。`dir` は光へ向かうベクトル (正規化不要)。</summary>
public class Renderer3dLight
{
    public Vec3 Dir = new Vec3(-0.4f, 1.0f, -0.55f);
    public Color Color = Color.Rgb(1.0f, 0.96f, 0.9f);
    public float Intensity = 1.25f;
}

/// <summary>hemispheric ambient の空色 (上) / 地面色 (下) と強度。</summary>
public class Renderer3dSky
{
    public Color Top = Color.Rgb(0.42f, 0.48f, 0.58f);
    public Color Bottom = Color.Rgb(0.20f, 0.18f, 0.16f);
    public float Intensity = 0.55f;
}

/// <summary>shadow map。`center`/`extent` は光のオルソ範囲 (world)。</summary>
public class Renderer3dShadow
{
    public bool Enabled = true;
    public int Size = 2048;
    public Vec3 Center = new Vec3(0, 0, 0);
    public float Extent = 12.0f;
    public float Bias = 0.004f;
}

/// <summary>SSAO (半解像度、depth 由来)。`radius` は view 空間。</summary>
public class Renderer3dSsao
{
    public bool Enabled = true;
    public float Radius = 0.6f;
    public float Strength = 0.85f;
}

/// <summary>bloom。`threshold` は HDR 輝度、`strength` は合成量。</summary>
public class Renderer3dBloom
{
    public bool Enabled = true;
    public float Threshold = 1.0f;
    public float Strength = 0.35f;
}

/// <summary>ポスト AA (FXAA)。</summary>
public class Renderer3dAa
{
    public bool Enabled = true;
}

/// <summary>距離 fog (`Renderer3d.fog` に代入して opt-in)。`density` は
/// 1/距離スケール。</summary>
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

/// <summary>depth エッジの輪郭線 (`Renderer3d.outline` に代入して opt-in)。
/// `threshold` は view 距離差。</summary>
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
/// forward + HDR の組み込みレンダラ。「メッシュを投げたら一発でいい絵」が目標。
///
/// <code>
/// var ren = new Renderer3d("main");
/// // 毎フレーム:
/// ren.begin(new Camera { eye = eye, target = tgt, fov = 38 });
/// ren.draw(mesh, model);
/// ren.draw(charMesh, m2, new Draw3dOpts { bones = packed });
/// ren.End(); // shadow → forward(HDR) → tonemap → swapchain
/// </code>
///
/// 既定で ON: 平行光源 + hemispheric ambient、shadow (PCF 3×3)、AgX tonemap。
/// 作風は効果別のフィールドで調整する (`light` / `sky` / `shadow` /
/// `exposure`)。offscreen と swapchain の y 反転差はここが吸収するので、
/// 利用側は気にしない。
///
/// `End()` 後の swapchain には
/// `Gfx.begin_pass(new PassOpts { target = Gfx.main_tex, load = Gfx.LOAD })`
/// で UI やテキストを重ね描きできる。
/// </summary>
public class Renderer3d
{
    // --- 埋め込み shader (pncm / pncmw 頂点レイアウト契約) -------------------
    // NOTE: slang の WGSL 出力は TEXCOORDn を @location(n) に割り当てるので、
    // TEXCOORD の番号は宣言位置に合わせる (ズレると wasm で attr が崩れる)。
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
        struct VSIn {
          float3 pos : POSITION;
          float3 normal : NORMAL;
          float3 color : COLOR;
          float2 mr : TEXCOORD3;
        };
        """
        + litVsBody
        + """

        [shader("vertex")] VSOut vs_main(VSIn i) {
          VSOut o;
          float4 wp4 = mul(u.model, float4(i.pos, 1.0f));
          o.pos = mul(u.mvp, float4(i.pos, 1.0f));
          o.wn = mul(u.model, float4(i.normal, 0.0f)).xyz;
          o.wp = wp4.xyz;
          o.lpos = mul(u.light_mvp, wp4);
          // 頂点色 / tint は sRGB authoring。ライティングは linear で行い AgX が
          // display に戻す。
          float3 srgb = i.color * u.tint.rgb;
          o.albedo = float4(pow(srgb, float3(2.2f, 2.2f, 2.2f)), u.tint.a);
          o.mr = i.mr;
          return o;
        }

        """;

    private static string litSkinnedVs = litVsCommon
        + """
          float4x4 bones[8];
        };
        ConstantBuffer<Uniforms> u;
        struct VSIn {
          float3 pos : POSITION;
          float3 normal : NORMAL;
          float3 color : COLOR;
          float2 mr : TEXCOORD3;
          float4 skin : TEXCOORD4; // j0, w0, j1, w1
        };
        """
        + litVsBody
        + """

        [shader("vertex")] VSOut vs_main(VSIn i) {
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

    // 誘電体/金属の分岐は 23_crane_game 由来。光方向・環境光を uniform 化し、
    // 平行光成分に shadow を掛ける。出力は HDR (クランプしない)。
    private static string litFs = """

        LUB_TEXTURE2D(shadow_map);
        struct FsU {
          float4 light_dir; // world, toward light (normalized)
          float4 light_col; // rgb * intensity
          float4 sky_col;   // hemispheric ambient (上), w = ambient 強度
          float4 ground_col; // hemispheric ambient (下)
          float4 cam_pos;   // world camera (specular 用)
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

        float shadow_factor(float4 lpos, float ndl) {
          if (f.shadow_p.z < 0.5f)
            return 1.0f;
          float3 ndc = lpos.xyz / lpos.w;
          float2 uv = ndc.xy * 0.5f + 0.5f;
          uv.y = 1.0f - uv.y; // shadow map stored y-down vs the lookup uv
          if (uv.x < 0.0f || uv.x > 1.0f || uv.y < 0.0f || uv.y > 1.0f || ndc.z < 0.0f ||
              ndc.z > 1.0f)
            return 1.0f;
          float texel = f.shadow_p.x;
          // slope-scaled: 面が光に平行なほど acne が出やすいので bias を増す
          float bias = f.shadow_p.y * (1.0f + (1.0f - saturate(ndl)) * 3.0f);
          float lit = 0.0f;
          for (int y = -1; y <= 1; ++y)
            for (int x = -1; x <= 1; ++x) {
              float closest =
                  LUB_SAMPLE_LOD(shadow_map, uv + float2(float(x), float(y)) * texel).r;
              lit += (ndc.z - bias <= closest) ? 1.0f : 0.0f;
            }
          return lit / 9.0f;
        }

        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          float3 n = normalize(i.wn);
          float3 l = f.light_dir.xyz;
          float metal = i.mr.x;
          float rough = i.mr.y;
          float sh = shadow_factor(i.lpos, dot(n, l));
          float up = n.y * 0.5f + 0.5f;
          float3 hemi = lerp(f.ground_col.rgb, f.sky_col.rgb, up) * f.sky_col.w;
          float3 v = normalize(f.cam_pos.xyz - i.wp);
          float3 hv = normalize(l + v);

          // 誘電体: half-lambert + hemispheric ambient + roughness で絞る specular
          float diff = dot(n, l) * 0.5f + 0.5f;
          float3 direct = f.light_col.rgb * (diff * diff) * sh;
          float spec =
              pow(max(dot(n, hv), 0.0f), 32.0f) * (1.0f - rough) * 0.5f * sh;
          float3 dielectric = i.albedo.rgb * (direct + hemi) + f.light_col.rgb * spec;

          // 金属: 上下グラデ環境 + 強い specular
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
        struct VSIn {
          float3 pos : POSITION;
          float3 normal : NORMAL;
          float3 color : COLOR;
          float2 mr : TEXCOORD3;
        };
        struct VSOut {
          float4 pos : SV_Position;
        };
        [shader("vertex")] VSOut vs_main(VSIn i) {
          VSOut o;
          o.pos = mul(u.light_mvp, mul(u.model, float4(i.pos, 1.0f)));
          return o;
        }

        """;

    private static string shadowSkinnedVs = """

        struct U {
          float4x4 light_mvp;
          float4x4 model;
          float4x4 bones[8];
        };
        ConstantBuffer<U> u;
        struct VSIn {
          float3 pos : POSITION;
          float3 normal : NORMAL;
          float3 color : COLOR;
          float2 mr : TEXCOORD3;
          float4 skin : TEXCOORD4;
        };
        struct VSOut {
          float4 pos : SV_Position;
        };
        [shader("vertex")] VSOut vs_main(VSIn i) {
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

    // depth-only pass: color attachment が無いので出力は捨てられる
    // (webgpu は fragment stage 自体が省かれる)。
    private static string shadowFs = """

        [shader("fragment")] float4 fs_main() : SV_Target {
          return float4(0.0f, 0.0f, 0.0f, 1.0f);
        }

        """;

    // 全 offscreen ポストパス共通の flip quad (uv が source texture と同向)。
    private static List<float> flipQuad = new List<float>
    {
        -1, -1, 0, 1,
         1, -1, 1, 1,
         1,  1, 1, 0,
        -1, -1, 0, 1,
         1,  1, 1, 0,
        -1,  1, 0, 0,
    };

    // SSAO: depth から view 位置を復元し、面法線は depth 微分から。半解像度。
    // カーネルは固定 12 サンプルの渦巻き (乱数なし = 決定的)。
    private static string ssaoFs = """

        LUB_TEXTURE2D(depth_tex);
        struct FsU {
          float4 pp;    // m0, m5abs, A (m10), B (m11)
          float4 ao_p;  // x = radius (view), y = strength, z = 1/w, w = 1/h
        };
        ConstantBuffer<FsU> f;
        struct FSIn {
          float2 uv : TEXCOORD0;
        };

        float3 view_pos(float2 uv) {
          float d = LUB_SAMPLE_LOD(depth_tex, uv).r;
          // LH 投影 (m10 = A, m11 = B < 0) の逆変換: z = B / (d - A)。d - A は常に負。
          float vz = f.pp.w / min(d - f.pp.z, -1e-6f);
          float x = (uv.x * 2.0f - 1.0f) * vz / f.pp.x;
          float y = (1.0f - uv.y * 2.0f) * vz / f.pp.y;
          return float3(x, y, vz);
        }

        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          float3 p = view_pos(i.uv);
          float3 n = normalize(cross(ddy(p), ddx(p)));
          // 12 点の渦巻きオフセット (screen 空間) を view radius でスケール
          float rpx = f.ao_p.x / p.z * f.pp.y * 0.5f; // 半径を uv スケールに
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
            // 半径内で手前に張り出す面だけを遮蔽としてカウント
            float range = saturate(1.0f - dist / f.ao_p.x);
            occ += saturate(ndotd - 0.02f) * range;
          }
          float ao = 1.0f - saturate(occ / 12.0f * 2.2f) * f.ao_p.y;
          return float4(ao, ao, ao, 1.0f);
        }

        """;

    // bloom 抽出: soft-knee threshold。
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

    // 縮小/拡大 (LINEAR サンプラ + 4 tap tent)。up は ADDITIVE blend で描く。
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

    // composite: scene * AO + bloom、fog、outline (どちらも opt-in、HDR 空間)。
    private static string compositeFs = """

        LUB_TEXTURE2D(scene);
        LUB_TEXTURE2D(ao_tex);
        LUB_TEXTURE2D(bloom_tex);
        LUB_TEXTURE2D(depth_tex);
        struct FsU {
          float4 pp;      // m0, m5abs, A, B (view 復元)
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
            // depth エッジ検出 (4 近傍)
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

    // FXAA (console 風の簡易版)。LDR に対して。
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

    // 素の blit (FXAA off 時の present)。
    private static string presentFs = """

        LUB_TEXTURE2D(scene);
        struct FSIn {
          float2 uv : TEXCOORD0;
        };
        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          return float4(LUB_SAMPLE_LOD(scene, i.uv).rgb, 1.0f);
        }

        """;

    private static string quadVs = """

        struct VSIn {
          float2 pos : POSITION;
          float2 uv : TEXCOORD0;
        };
        struct VSOut {
          float2 uv : TEXCOORD0;
          float4 pos : SV_Position;
        };
        [shader("vertex")] VSOut vs_main(VSIn i) {
          VSOut o;
          o.pos = float4(i.pos, 0.0f, 1.0f);
          o.uv = i.uv;
          return o;
        }

        """;

    // AgX (minimal fit)。HDR → display。exposure は stop (2^n)。
    private static string tonemapFs = """

        LUB_TEXTURE2D(scene);
        struct FsU {
          float4 grade; // x = exposure (stops), y = vignette, z = dither, w = 画面高
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
          // punchy look: わずかな締め + 彩度戻し (AgX は素だと眠い)
          o = pow(o, float3(1.08f, 1.08f, 1.08f));
          float lum = dot(o, float3(0.2126f, 0.7152f, 0.0722f));
          o = lum + (o - lum) * 1.28f;
          // vignette (grade.y = 強度)
          float2 d2 = i.uv - 0.5f;
          o *= 1.0f - dot(d2, d2) * 2.0f * f.grade.y;
          // triangular dither (grade.z = 1 で on)。座標ハッシュなので決定的。
          float h = frac(sin(dot(i.uv * f.grade.w, float2(12.9898f, 78.233f))) * 43758.5453f);
          o += (h - 0.5f) * (2.0f / 255.0f) * f.grade.z;
          return float4(saturate(o), 1.0f);
        }

        """;

    // swapchain 向け present quad (clip y = -1 → uv.y = 0)。offscreen 側は
    // proj.m[5] 反転で screen 向きに描かれているので、この 1 枚で向きが合う。
    private static List<float> presentQuad = new List<float>
    {
        -1, -1, 0, 0,
         1, -1, 1, 0,
         1,  1, 1, 1,
        -1, -1, 0, 0,
         1,  1, 1, 1,
        -1,  1, 0, 1,
    };

    // --- 公開オプション -------------------------------------------------------

    /// <summary>平行光源。`dir` は光へ向かうベクトル (正規化不要)。</summary>
    public Renderer3dLight Light = new Renderer3dLight();

    /// <summary>hemispheric ambient の空色 (上) / 地面色 (下) と強度。</summary>
    public Renderer3dSky Sky = new Renderer3dSky();

    /// <summary>shadow map。`center`/`extent` は光のオルソ範囲 (world)。</summary>
    public Renderer3dShadow Shadow = new Renderer3dShadow();

    /// <summary>露出 (stop)。+1 で 2 倍明るい。</summary>
    public float Exposure = 0.0f;

    /// <summary>HDR クリア色 (背景)。</summary>
    public Color Background = Color.Rgb(0.09f, 0.12f, 0.15f);

    /// <summary>SSAO (半解像度、depth 由来)。`radius` は view 空間。</summary>
    public Renderer3dSsao Ssao = new Renderer3dSsao();

    /// <summary>bloom。`threshold` は HDR 輝度、`strength` は合成量。</summary>
    public Renderer3dBloom Bloom = new Renderer3dBloom();

    /// <summary>ポスト AA (FXAA)。</summary>
    public Renderer3dAa Aa = new Renderer3dAa();

    /// <summary>8bit バンディング対策の triangular dither。</summary>
    public bool Dither = true;

    /// <summary>周辺減光 0..1 (0 = off)。</summary>
    public float Vignette = 0.0f;

    /// <summary>距離 fog (opt-in)。`density` は 1/距離スケール。</summary>
    public Renderer3dFog? Fog = null;

    /// <summary>depth エッジの輪郭線 (opt-in)。`threshold` は view 距離差。</summary>
    public Renderer3dOutline? Outline = null;

    /// <summary>中間バッファの確認用: "ao" / "bloom" / "hdr" を swapchain に
    /// 直接出す。</summary>
    public string? DebugView = null;

    /// <summary>world → clip (y-flip なし)。スクリーン座標への投影 (HUD 追従等)
    /// 用。begin() が設定する。利用側は読み取り専用。</summary>
    public Mat4? ViewProj = null;

    /// <summary>view 行列 (begin() で確定)。差し替え shader の view-space
    /// 計算用。利用側は読み取り専用。</summary>
    public Mat4? ViewMat = null;

    private string key;
    private List<Renderer3dDrawCmd> draws = new List<Renderer3dDrawCmd>();
    private Mat4? view = null;
    private Mat4? proj = null;
    private Mat4? vp = null;
    private Vec3 eye = new Vec3(0, 0, 0);
    private BufferRef? flipQuadBuf = null;

    public Renderer3d(string key)
    {
        this.key = key;
    }

    /// <summary>Phys3d の pose (x,y,z,qx,qy,qz,qw) → model 行列。</summary>
    public static Mat4 PoseMat(Pose3d pose)
    {
        return Mat4.Translate(new Vec3(pose.X, pose.Y, pose.Z))
            * new Quat(pose.Qx, pose.Qy, pose.Qz, pose.Qw).ToMat4();
    }

    /// <summary>フレーム開始。カメラを確定し draw 列を空にする。</summary>
    public void Begin(Camera cam)
    {
        var up = cam.Up ?? new Vec3(0, 1, 0);
        var fov = cam.Fov ?? 60.0f;
        var near = cam.Near ?? 0.1f;
        var far = cam.Far ?? 100.0f;
        Gfx.Size(out var w, out var h);
        var p = Mat4.PerspectiveLh(fov, (float)w / h, near, far);
        var v = Mat4.LookAtLh(cam.Eye, cam.Target, up);
        view = v;
        ViewMat = v;
        ViewProj = p * v;
        // offscreen target は swapchain と違い y-flip されないので、clip y を
        // あらかじめ反転して screen 向きで描く (present quad と対)。
        p.M[5] = -p.M[5];
        proj = p;
        vp = p * v;
        eye = cam.Eye;
        // draws を空にする。List.Clear() は tcs が
        // `(function() ... end)()` を emit し、直前の代入文と連結されて
        // 関数呼び出しに誤解釈される (Lua の文区切り曖昧性) ため使わない。
        draws = new List<Renderer3dDrawCmd>();
    }

    /// <summary>描画を記録する (実行は `End()`)。</summary>
    public void Draw(Mesh3d? mesh, Mat4 model, Draw3dOpts? opts = null)
    {
        if (mesh == null || !mesh.Ready())
            return;
        var tint = new List<float> { 1.0f, 1.0f, 1.0f, 1.0f };
        var blend = Gfx.Blend.None;
        List<float>? bones = null;
        ShaderRef? shader = null;
        Dictionary<string, TextureRef>? textures = null;
        Dictionary<string, object>? uniforms = null;
        if (opts != null)
        {
            var t = opts.Tint;
            if (t != null)
                tint = new List<float> { t.R, t.G, t.B, t.A };
            blend = opts.Blend ?? Gfx.Blend.None;
            bones = opts.Bones;
            shader = opts.Shader;
            textures = opts.Textures;
            uniforms = opts.Uniforms;
        }
        draws.Add(new Renderer3dDrawCmd(mesh, model, tint, blend, bones,
            shader, textures, uniforms));
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
        // dir が真上のときの up 退避
        var up = Math.Abs(Light.Dir.Y) * inv > 0.99f
            ? new Vec3(0, 0, 1)
            : new Vec3(0, 1, 0);
        var lview = Mat4.LookAtLh(leye, Shadow.Center, up);
        return Mat4.OrthoLh(Shadow.Extent * 2.0f, Shadow.Extent * 2.0f, 0.1f,
            dist * 2.0f) * lview;
    }

    // mesh が null なら resolve は
    // 呼ばれないが、Bones.pack の契約 (resolve 非 null) を保つためダミーを渡す。
    private static List<float> IdentityBones()
    {
        return Bones.Pack(null, (name, px, py, pz) => null);
    }

    private void ShadowPass(Mat4 lmvp, ShaderRef shStatic, ShaderRef shSkinned,
        TextureRef shadowMap)
    {
        Gfx.BeginPass(new PassOpts { DepthTarget = shadowMap, ClearDepth = 1.0f });
        var lm = lmvp.M;
        foreach (var d in draws)
        {
            if (d.Blend != Gfx.Blend.None)
                continue; // 半透明は影を落とさない
            var vb = d.Mesh.Vb;
            var ib = d.Mesh.Ib;
            if (vb == null || ib == null)
                continue;
            var u = new Dictionary<string, object>
            {
                ["light_mvp"] = lm,
                ["model"] = d.Model.M,
            };
            if (d.Mesh.Skinned)
                u["bones"] = d.Bones ?? IdentityBones();
            Gfx.Draw(d.Mesh.IndexCount, new Dictionary<string, object>
            {
                ["verts"] = vb,
                ["indices"] = ib,
                ["uniforms"] = u,
            }, new DrawOpts
            {
                Shader = d.Mesh.Skinned ? shSkinned : shStatic,
                Depth = true,
                DepthWrite = true,
                Cull = Gfx.Cull.None,
            });
        }
        Gfx.EndPass();
    }

    private Dictionary<string, object> LitUniforms(Renderer3dDrawCmd d,
        Mat4 vp, Mat4 lmvp, float texel)
    {
        // (差し替え shader の追加 uniform は末尾でマージ)
        var u = new Dictionary<string, object>
        {
            ["mvp"] = (vp * d.Model).M,
            ["model"] = d.Model.M,
            ["light_mvp"] = lmvp.M,
            ["tint"] = d.Tint,
            ["light_dir"] = LightDirTable(),
            ["light_col"] = new List<float>
            {
                Light.Color.R * Light.Intensity,
                Light.Color.G * Light.Intensity,
                Light.Color.B * Light.Intensity,
                0.0f,
            },
            ["sky_col"] = new List<float>
                { Sky.Top.R, Sky.Top.G, Sky.Top.B, Sky.Intensity },
            ["ground_col"] = new List<float>
                { Sky.Bottom.R, Sky.Bottom.G, Sky.Bottom.B, 0.0f },
            ["cam_pos"] = new List<float> { eye.X, eye.Y, eye.Z, 0.0f },
            ["shadow_p"] = new List<float>
                { texel, Shadow.Bias, Shadow.Enabled ? 1.0f : 0.0f, 0.0f },
        };
        if (d.Mesh.Skinned)
            u["bones"] = d.Bones ?? IdentityBones();
        if (d.Uniforms != null)
        {
            foreach (var kv in d.Uniforms)
                u[kv.Key] = kv.Value;
        }
        return u;
    }

    private List<float> LightDirTable()
    {
        var len = (float)Math.Sqrt(Light.Dir.X * Light.Dir.X
            + Light.Dir.Y * Light.Dir.Y + Light.Dir.Z * Light.Dir.Z);
        var inv = len > 1e-6f ? 1.0f / len : 1.0f;
        return new List<float>
            { Light.Dir.X * inv, Light.Dir.Y * inv, Light.Dir.Z * inv, 0.0f };
    }

    // flip quad で target 全面に 1 パス描く。
    private void Blit(TextureRef target, ShaderRef shader,
        Dictionary<string, object> bindings, Gfx.LoadAction? load = null,
        Gfx.Blend? blend = null)
    {
        var fq = flipQuadBuf;
        if (fq == null)
            return;
        var opts = new PassOpts { Target = target };
        if (load != null)
            opts.Load = load;
        Gfx.BeginPass(opts);
        bindings["verts"] = fq;
        Gfx.Draw(6, bindings, new DrawOpts
        {
            Shader = shader,
            Depth = false,
            Cull = Gfx.Cull.None,
            Blend = blend ?? Gfx.Blend.None,
        });
        Gfx.EndPass();
    }

    /// <summary>記録した draw 列を実行して swapchain まで出す
    /// (end は Lua キーワードのため End)。</summary>
    public void End()
    {
        var vp = this.vp;
        var proj = this.proj;
        if (vp == null || proj == null)
            return;
        Gfx.Size(out var w, out var h);
        var rtVer = w * 65536 + h;

        var litStatic = Gfx.UseShader(key + "_lit_s", litStaticVs, litFs, 1);
        var litSkinned = Gfx.UseShader(key + "_lit_k", litSkinnedVs, litFs, 1);
        var shStatic = Gfx.UseShader(key + "_sh_s", shadowStaticVs, shadowFs, 1);
        var shSkinned = Gfx.UseShader(key + "_sh_k", shadowSkinnedVs, shadowFs, 1);
        var tonemap = Gfx.UseShader(key + "_tm", quadVs, tonemapFs, 1);
        var ssaoSh = Gfx.UseShader(key + "_ssao", quadVs, ssaoFs, 1);
        var brightSh = Gfx.UseShader(key + "_br", quadVs, brightFs, 1);
        var tentSh = Gfx.UseShader(key + "_tent", quadVs, blitTentFs, 1);
        var compSh = Gfx.UseShader(key + "_comp", quadVs, compositeFs, 1);
        var fxaaSh = Gfx.UseShader(key + "_fxaa", quadVs, fxaaFs, 1);
        var presentSh = Gfx.UseShader(key + "_pr", quadVs, presentFs, 1);
        if (litStatic == null || litSkinned == null || shStatic == null
            || shSkinned == null || tonemap == null || ssaoSh == null
            || brightSh == null || tentSh == null || compSh == null
            || fxaaSh == null || presentSh == null)
            return;

        var hdr = Gfx.UseTexture(key + "_hdr", w, h, Gfx.PixelFormat.Rgba16f, null, rtVer,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp });
        var depth = Gfx.UseTexture(key + "_depth", w, h, Gfx.PixelFormat.Depth32f, null,
            rtVer, new TextureOpts { Target = true, Wrap = Gfx.Wrap.Clamp });
        var shadowMap = Gfx.UseTexture(key + "_sm", Shadow.Size, Shadow.Size,
            Gfx.PixelFormat.Depth32f, null, Shadow.Size,
            new TextureOpts { Target = true, Wrap = Gfx.Wrap.Clamp });
        var quad = Gfx.UseBuffer(key + "_quad", Gfx.BufferType.Vertex, presentQuad, 1);
        flipQuadBuf = Gfx.UseBuffer(key + "_fquad", Gfx.BufferType.Vertex, flipQuad, 1);
        if (hdr == null || depth == null || shadowMap == null || quad == null
            || flipQuadBuf == null)
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
            // background も sRGB authoring → linear で HDR に置く
            ClearColor = new float[]
            {
                (float)Math.Pow(Background.R, 2.2f),
                (float)Math.Pow(Background.G, 2.2f),
                (float)Math.Pow(Background.B, 2.2f),
                1.0f,
            },
            ClearDepth = 1.0f,
        });
        // opaque → blend の順
        for (int phase = 0; phase < 2; phase++)
        {
            foreach (var d in draws)
            {
                bool isBlend = d.Blend != Gfx.Blend.None;
                if ((phase == 0) == isBlend)
                    continue;
                var vb = d.Mesh.Vb;
                var ib = d.Mesh.Ib;
                if (vb == null || ib == null)
                    continue;
                var shader = d.Shader
                    ?? (d.Mesh.Skinned ? litSkinned : litStatic);
                var bindings = new Dictionary<string, object>
                {
                    ["verts"] = vb,
                    ["indices"] = ib,
                    ["shadow_map"] = shadowMap,
                    ["uniforms"] = LitUniforms(d, vp, lmvp, texel),
                };
                if (d.Textures != null)
                {
                    foreach (var kv in d.Textures)
                        bindings[kv.Key] = kv.Value;
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

        // proj は m[5] を反転済みなので |m5| を渡す
        var projP = new List<float>
            { proj.M[0], Math.Abs(proj.M[5]), proj.M[10], proj.M[11] };

        // SSAO (半解像度)
        TextureRef? aoTex = null;
        if (Ssao.Enabled)
        {
            int aw = (int)Math.Floor(w / 2.0f);
            int ah = (int)Math.Floor(h / 2.0f);
            aoTex = Gfx.UseTexture(key + "_ao", aw, ah, Gfx.PixelFormat.R8, null, rtVer,
                new TextureOpts { Target = true, Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp });
            if (aoTex != null)
            {
                Blit(aoTex, ssaoSh, new Dictionary<string, object>
                {
                    ["depth_tex"] = depth,
                    ["uniforms"] = new Dictionary<string, object>
                    {
                        ["pp"] = projP,
                        ["ao_p"] = new List<float>
                            { Ssao.Radius, Ssao.Strength, 1.0f / aw, 1.0f / ah },
                    },
                });
            }
        }

        // bloom: bright → 縮小 3 段 → ADDITIVE で逆順に戻す
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
                bw = (int)Math.Floor(bw / 2.0f);
                bh = (int)Math.Floor(bh / 2.0f);
                if (bw < 8 || bh < 8)
                    break;
                var t = Gfx.UseTexture(key + "_bl" + li, bw, bh, Gfx.PixelFormat.Rgba16f,
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
                Blit(texs[0], brightSh, new Dictionary<string, object>
                {
                    ["scene"] = hdr,
                    ["uniforms"] = new Dictionary<string, object>
                    {
                        ["bl"] = new List<float> { Bloom.Threshold, 0.5f, 0.0f, 0.0f },
                    },
                });
                for (int li = 1; li < texs.Count; li++)
                {
                    Blit(texs[li], tentSh, new Dictionary<string, object>
                    {
                        ["scene"] = texs[li - 1],
                        ["uniforms"] = new Dictionary<string, object>
                        {
                            ["st"] = new List<float>
                                { 1.0f / ws[li - 1], 1.0f / hs[li - 1], 1.0f, 0.0f },
                        },
                    });
                }
                int j = texs.Count - 1;
                while (j > 0)
                {
                    Blit(texs[j - 1], tentSh, new Dictionary<string, object>
                    {
                        ["scene"] = texs[j],
                        ["uniforms"] = new Dictionary<string, object>
                        {
                            ["st"] = new List<float>
                                { 1.0f / ws[j], 1.0f / hs[j], 0.7f, 0.0f },
                        },
                    }, Gfx.LoadAction.Load, Gfx.Blend.Additive);
                    j--;
                }
                bloomTex = texs[0];
            }
        }

        // composite (AO 乗算 + bloom 加算 + fog / outline)
        var post = Gfx.UseTexture(key + "_post", w, h, Gfx.PixelFormat.Rgba16f, null,
            rtVer, new TextureOpts { Target = true, Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp });
        if (post == null)
            return;
        var fog = this.Fog;
        var outline = this.Outline;
        bool fogOn = fog != null;
        bool olOn = outline != null;
        Blit(post, compSh, new Dictionary<string, object>
        {
            ["scene"] = hdr,
            ["ao_tex"] = aoTex ?? hdr,
            ["bloom_tex"] = bloomTex ?? hdr,
            ["depth_tex"] = depth,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["pp"] = projP,
                ["en"] = new List<float>
                {
                    aoTex != null ? 1.0f : 0.0f,
                    bloomTex != null ? Bloom.Strength : 0.0f,
                    fogOn ? 1.0f : 0.0f,
                    olOn ? 1.0f : 0.0f,
                },
                ["fog_col"] = fog != null
                    ? new List<float>
                    {
                        (float)Math.Pow(fog.Color.R, 2.2f),
                        (float)Math.Pow(fog.Color.G, 2.2f),
                        (float)Math.Pow(fog.Color.B, 2.2f),
                        fog.Density,
                    }
                    : new List<float> { 0.0f, 0.0f, 0.0f, 0.0f },
                ["ol"] = outline != null
                    ? new List<float>
                    {
                        (float)Math.Pow(outline.Color.R, 2.2f),
                        (float)Math.Pow(outline.Color.G, 2.2f),
                        (float)Math.Pow(outline.Color.B, 2.2f),
                        outline.Threshold,
                    }
                    : new List<float> { 0.0f, 0.0f, 0.0f, 1.0f },
                ["px"] = new List<float> { 1.0f / w, 1.0f / h, 0.0f, 0.0f },
            },
        });

        // tonemap (+vignette +dither) → LDR
        var ldr = Gfx.UseTexture(key + "_ldr", w, h, Gfx.PixelFormat.Rgba8, null, rtVer,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp });
        if (ldr == null)
            return;
        Blit(ldr, tonemap, new Dictionary<string, object>
        {
            ["scene"] = post,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["grade"] = new List<float>
                    { Exposure, Vignette, Dither ? 1.0f : 0.0f, h },
            },
        });

        // FXAA (or 素通し) → swapchain
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
                Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
                Gfx.Draw(6, new Dictionary<string, object>
                { ["verts"] = quad, ["scene"] = dbg },
                    new DrawOpts { Shader = presentSh, Depth = false, Cull = Gfx.Cull.None });
                Gfx.EndPass();
                return;
            }
        }
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        if (Aa.Enabled)
        {
            Gfx.Draw(6, new Dictionary<string, object>
            {
                ["verts"] = quad,
                ["scene"] = ldr,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["px"] = new List<float> { 1.0f / w, 1.0f / h, 0.0f, 0.0f },
                },
            }, new DrawOpts { Shader = fxaaSh, Depth = false, Cull = Gfx.Cull.None });
        }
        else
        {
            Gfx.Draw(6, new Dictionary<string, object>
            { ["verts"] = quad, ["scene"] = ldr },
                new DrawOpts { Shader = presentSh, Depth = false, Cull = Gfx.Cull.None });
        }
        Gfx.EndPass();
    }
}
