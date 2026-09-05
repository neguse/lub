// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Renderer3d.hx と対)。
// Haxe 版からの写し方:
// - typedef (Draw3dOpts / Camera) と匿名構造体フィールド (light / sky /
//   shadow / ssao / bloom / aa / fog / outline) は options class 化。
//   optional は nullable フィールド + ??、必須 2 フィールドの fog / outline
//   だけはコンストラクタで受ける。private typedef DrawCmd は
//   Renderer3dDrawCmd (内部用)。
// - Reflect.fields/field/setField による uniform / texture の動的マージは
//   Dictionary<string, object> / Dictionary<string, TextureRef> の foreach で
//   bindings dict へ代入する (tcs の Dictionary は素の Lua table なので
//   wire format はそのまま)。
// - lua.Table.fromArray は List<double> 直。Mat4.m (List<double>) も直渡し。
// - Dynamic は ShaderRef / TextureRef / BufferRef / List / Dictionary /
//   Pose3d に型付け。bones は Bones.pack() の返す List<double>。
// - Haxe 版の Bones.pack(null, null) は resolve が null 許容でないので
//   identityBones() (ダミー resolve の lambda) に置き換える。
// - sz.w >> 1 等の bit shift は tcs 未対応なので Math.Floor(x / 2.0)。
// - use_texture / use_buffer / mesh.vb の null は早期 return / continue で
//   ガードする (cs-lib 慣例。Haxe 版は nil のまま突っ込んで実行時に落ちる)。
// - viewProj / viewMat は Haxe の (default, null) に対応する機構が無いので
//   public フィールド (書くのは begin() だけ、利用側は読み取り専用扱い)。
// - shadowPass の未使用引数 shadowSize は削除。litUniforms は nullable の
//   フィールド vp を使う代わりに End() で narrow 済みの vp を引数で受ける。
// メンバー名は Haxe 版 API と揃える (--no-naming-check でビルドされる)。
// Haxe 版の end() だけは同名にできない: end は Lua キーワードで、tcs が
// 宣言をそのまま `function Renderer3d:end` と emit して不正 Lua になるため
// End に改名している (MeshText の char → Char と同じ扱い)。

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
    public List<double>? Bones;

    /// <summary>material 差し替え。頂点レイアウトと uniform 名は既定 shader
    /// と同じ契約 (必要な uniform 名だけ宣言すればよい)。</summary>
    public ShaderRef? Shader;

    /// <summary>差し替え shader 用の追加テクスチャ (名前 → TextureRef)。</summary>
    public Dictionary<string, TextureRef>? Textures;

    /// <summary>差し替え shader 用の追加 uniform (名前 → List&lt;double&gt;)。
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
    public double? Fov;

    public double? Near;
    public double? Far;
}

/// <summary>Renderer3d の per-draw 記録 (内部用)。</summary>
public class Renderer3dDrawCmd
{
    public Mesh3d Mesh;
    public Mat4 Model;
    public List<double> Tint;
    public Gfx.Blend Blend;
    public List<double>? Bones;
    public ShaderRef? Shader;
    public Dictionary<string, TextureRef>? Textures;
    public Dictionary<string, object>? Uniforms;

    public Renderer3dDrawCmd(Mesh3d mesh, Mat4 model, List<double> tint,
        Gfx.Blend blend, List<double>? bones, ShaderRef? shader,
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
    public Vec3 Dir = new Vec3(-0.4, 1.0, -0.55);
    public Color Color = Color.Rgb(1.0, 0.96, 0.9);
    public double Intensity = 1.25;
}

/// <summary>hemispheric ambient の空色 (上) / 地面色 (下) と強度。</summary>
public class Renderer3dSky
{
    public Color Top = Color.Rgb(0.42, 0.48, 0.58);
    public Color Bottom = Color.Rgb(0.20, 0.18, 0.16);
    public double Intensity = 0.55;
}

/// <summary>shadow map。`center`/`extent` は光のオルソ範囲 (world)。</summary>
public class Renderer3dShadow
{
    public bool Enabled = true;
    public int Size = 2048;
    public Vec3 Center = new Vec3(0, 0, 0);
    public double Extent = 12.0;
    public double Bias = 0.004;
}

/// <summary>SSAO (半解像度、depth 由来)。`radius` は view 空間。</summary>
public class Renderer3dSsao
{
    public bool Enabled = true;
    public double Radius = 0.6;
    public double Strength = 0.85;
}

/// <summary>bloom。`threshold` は HDR 輝度、`strength` は合成量。</summary>
public class Renderer3dBloom
{
    public bool Enabled = true;
    public double Threshold = 1.0;
    public double Strength = 0.35;
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
    public double Density;

    public Renderer3dFog(Color color, double density)
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
    public double Threshold;

    public Renderer3dOutline(Color color, double threshold)
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
          float4 wp4 = mul(u.model, float4(i.pos, 1.0));
          o.pos = mul(u.mvp, float4(i.pos, 1.0));
          o.wn = mul(u.model, float4(i.normal, 0.0)).xyz;
          o.wp = wp4.xyz;
          o.lpos = mul(u.light_mvp, wp4);
          // 頂点色 / tint は sRGB authoring。ライティングは linear で行い AgX が
          // display に戻す。
          float3 srgb = i.color * u.tint.rgb;
          o.albedo = float4(pow(srgb, float3(2.2, 2.2, 2.2)), u.tint.a);
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
          float4 p4 = float4(i.pos, 1.0);
          float3 sp =
              (mul(u.bones[j0], p4) * i.skin.y + mul(u.bones[j1], p4) * i.skin.w).xyz;
          float3 sn = mul((float3x3)u.bones[j0], i.normal) * i.skin.y +
                      mul((float3x3)u.bones[j1], i.normal) * i.skin.w;
          float4 wp4 = mul(u.model, float4(sp, 1.0));
          o.pos = mul(u.mvp, float4(sp, 1.0));
          o.wn = mul(u.model, float4(sn, 0.0)).xyz;
          o.wp = wp4.xyz;
          o.lpos = mul(u.light_mvp, wp4);
          float3 srgb = i.color * u.tint.rgb;
          o.albedo = float4(pow(srgb, float3(2.2, 2.2, 2.2)), u.tint.a);
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
          if (f.shadow_p.z < 0.5)
            return 1.0;
          float3 ndc = lpos.xyz / lpos.w;
          float2 uv = ndc.xy * 0.5 + 0.5;
          uv.y = 1.0 - uv.y; // shadow map stored y-down vs the lookup uv
          if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || ndc.z < 0.0 ||
              ndc.z > 1.0)
            return 1.0;
          float texel = f.shadow_p.x;
          // slope-scaled: 面が光に平行なほど acne が出やすいので bias を増す
          float bias = f.shadow_p.y * (1.0 + (1.0 - saturate(ndl)) * 3.0);
          float lit = 0.0;
          for (int y = -1; y <= 1; ++y)
            for (int x = -1; x <= 1; ++x) {
              float closest =
                  LUB_SAMPLE_LOD(shadow_map, uv + float2(float(x), float(y)) * texel).r;
              lit += (ndc.z - bias <= closest) ? 1.0 : 0.0;
            }
          return lit / 9.0;
        }

        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          float3 n = normalize(i.wn);
          float3 l = f.light_dir.xyz;
          float metal = i.mr.x;
          float rough = i.mr.y;
          float sh = shadow_factor(i.lpos, dot(n, l));
          float up = n.y * 0.5 + 0.5;
          float3 hemi = lerp(f.ground_col.rgb, f.sky_col.rgb, up) * f.sky_col.w;
          float3 v = normalize(f.cam_pos.xyz - i.wp);
          float3 hv = normalize(l + v);

          // 誘電体: half-lambert + hemispheric ambient + roughness で絞る specular
          float diff = dot(n, l) * 0.5 + 0.5;
          float3 direct = f.light_col.rgb * (diff * diff) * sh;
          float spec =
              pow(max(dot(n, hv), 0.0), 32.0) * (1.0 - rough) * 0.5 * sh;
          float3 dielectric = i.albedo.rgb * (direct + hemi) + f.light_col.rgb * spec;

          // 金属: 上下グラデ環境 + 強い specular
          float3 env = lerp(f.ground_col.rgb * 0.8, f.sky_col.rgb * 1.6, up);
          float3 metallic = env * lerp(i.albedo.rgb, float3(1.0, 1.0, 1.0), 0.5);
          metallic +=
              f.light_col.rgb * pow(max(dot(n, hv), 0.0), 64.0) * (1.0 - rough) * 1.2 * sh;

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
          o.pos = mul(u.light_mvp, mul(u.model, float4(i.pos, 1.0)));
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
          float4 p4 = float4(i.pos, 1.0);
          float3 sp =
              (mul(u.bones[j0], p4) * i.skin.y + mul(u.bones[j1], p4) * i.skin.w).xyz;
          o.pos = mul(u.light_mvp, mul(u.model, float4(sp, 1.0)));
          return o;
        }

        """;

    // depth-only pass: color attachment が無いので出力は捨てられる
    // (webgpu は fragment stage 自体が省かれる)。
    private static string shadowFs = """

        [shader("fragment")] float4 fs_main() : SV_Target {
          return float4(0.0, 0.0, 0.0, 1.0);
        }

        """;

    // 全 offscreen ポストパス共通の flip quad (uv が source texture と同向)。
    private static List<double> flipQuad = new List<double>
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
          float vz = f.pp.w / min(d - f.pp.z, -1e-6);
          float x = (uv.x * 2.0 - 1.0) * vz / f.pp.x;
          float y = (1.0 - uv.y * 2.0) * vz / f.pp.y;
          return float3(x, y, vz);
        }

        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          float3 p = view_pos(i.uv);
          float3 n = normalize(cross(ddy(p), ddx(p)));
          // 12 点の渦巻きオフセット (screen 空間) を view radius でスケール
          float rpx = f.ao_p.x / p.z * f.pp.y * 0.5; // 半径を uv スケールに
          float occ = 0.0;
          float ang = 2.399963; // golden angle
          for (int k = 0; k < 12; ++k) {
            float fk = (float(k) + 0.5) / 12.0;
            float r = sqrt(fk) * rpx;
            float a = float(k) * ang;
            float2 duv = float2(cos(a) * r, sin(a) * r);
            float3 q = view_pos(i.uv + duv);
            float3 dq = q - p;
            float dist = length(dq);
            float ndotd = dot(n, dq / max(dist, 1e-6));
            // 半径内で手前に張り出す面だけを遮蔽としてカウント
            float range = saturate(1.0 - dist / f.ao_p.x);
            occ += saturate(ndotd - 0.02) * range;
          }
          float ao = 1.0 - saturate(occ / 12.0 * 2.2) * f.ao_p.y;
          return float4(ao, ao, ao, 1.0);
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
          soft = soft * soft / (4.0 * max(knee, 1e-4));
          float w = max(soft, lum - f.bl.x) / max(lum, 1e-4);
          return float4(c * saturate(w), 1.0);
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
          return float4(c * 0.25 * f.st.z, 1.0);
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
          return f.pp.w / min(d - f.pp.z, -1e-6);
        }

        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          float3 c = LUB_SAMPLE_LOD(scene, i.uv).rgb;
          if (f.en.x > 0.5)
            c *= LUB_SAMPLE_LOD(ao_tex, i.uv).r;
          c += LUB_SAMPLE_LOD(bloom_tex, i.uv).rgb * f.en.y;
          float vz = view_z(i.uv);
          if (f.en.w > 0.5) {
            // depth エッジ検出 (4 近傍)
            float2 t = f.px.xy;
            float zn = view_z(i.uv + float2(0.0, -t.y));
            float zs = view_z(i.uv + float2(0.0, t.y));
            float ze = view_z(i.uv + float2(t.x, 0.0));
            float zw = view_z(i.uv + float2(-t.x, 0.0));
            float edge = max(max(abs(zn - vz), abs(zs - vz)), max(abs(ze - vz), abs(zw - vz)));
            float o = saturate((edge - f.ol.w) / f.ol.w);
            c = lerp(c, f.ol.rgb, saturate(o) * 0.85);
          }
          if (f.en.z > 0.5) {
            float fogf = 1.0 - exp2(-vz * f.fog_col.w);
            c = lerp(c, f.fog_col.rgb, saturate(fogf));
          }
          return float4(c, 1.0);
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
        float luma(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
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
          if (lMax - lMin < max(0.0312, lMax * 0.125))
            return float4(cM, 1.0);
          float2 dir = float2(-((lNW + lNE) - (lSW + lSE)), (lNW + lSW) - (lNE + lSE));
          float dirReduce = max((lNW + lNE + lSW + lSE) * 0.03125, 0.0078125);
          float rcpMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
          dir = clamp(dir * rcpMin, float2(-8.0, -8.0), float2(8.0, 8.0)) * t;
          float3 a = 0.5 * (LUB_SAMPLE_LOD(scene, i.uv + dir * (1.0 / 3.0 - 0.5)).rgb +
                            LUB_SAMPLE_LOD(scene, i.uv + dir * (2.0 / 3.0 - 0.5)).rgb);
          float3 b = a * 0.5 + 0.25 * (LUB_SAMPLE_LOD(scene, i.uv + dir * -0.5).rgb +
                                       LUB_SAMPLE_LOD(scene, i.uv + dir * 0.5).rgb);
          float lB = luma(b);
          return float4((lB < lMin || lB > lMax) ? a : b, 1.0);
        }

        """;

    // 素の blit (FXAA off 時の present)。
    private static string presentFs = """

        LUB_TEXTURE2D(scene);
        struct FSIn {
          float2 uv : TEXCOORD0;
        };
        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          return float4(LUB_SAMPLE_LOD(scene, i.uv).rgb, 1.0);
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
          o.pos = float4(i.pos, 0.0, 1.0);
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
          return 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x +
                 0.4298 * x2 + 0.1191 * x - 0.00232;
        }

        [shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
          float3 c = LUB_SAMPLE_LOD(scene, i.uv).rgb;
          c *= exp2(f.grade.x);
          // AgX inset matrix
          float3 v = float3(0.842479 * c.r + 0.0784336 * c.g + 0.0792237 * c.b,
                            0.0423282 * c.r + 0.878468 * c.g + 0.0791661 * c.b,
                            0.0423756 * c.r + 0.0784336 * c.g + 0.879142 * c.b);
          // log2 encode
          float min_ev = -12.47393;
          float max_ev = 4.026069;
          v = clamp(log2(max(v, 1e-10)), min_ev, max_ev);
          v = (v - min_ev) / (max_ev - min_ev);
          v = agx_contrast(v);
          // outset matrix
          float3 o = float3(1.19688 * v.r - 0.0980209 * v.g - 0.0990297 * v.b,
                            -0.0528968 * v.r + 1.15190 * v.g - 0.0989612 * v.b,
                            -0.0529716 * v.r - 0.0980434 * v.g + 1.15107 * v.b);
          o = saturate(o);
          // punchy look: わずかな締め + 彩度戻し (AgX は素だと眠い)
          o = pow(o, float3(1.08, 1.08, 1.08));
          float lum = dot(o, float3(0.2126, 0.7152, 0.0722));
          o = lum + (o - lum) * 1.28;
          // vignette (grade.y = 強度)
          float2 d2 = i.uv - 0.5;
          o *= 1.0 - dot(d2, d2) * 2.0 * f.grade.y;
          // triangular dither (grade.z = 1 で on)。座標ハッシュなので決定的。
          float h = frac(sin(dot(i.uv * f.grade.w, float2(12.9898, 78.233))) * 43758.5453);
          o += (h - 0.5) * (2.0 / 255.0) * f.grade.z;
          return float4(saturate(o), 1.0);
        }

        """;

    // swapchain 向け present quad (clip y = -1 → uv.y = 0)。offscreen 側は
    // proj.m[5] 反転で screen 向きに描かれているので、この 1 枚で向きが合う。
    private static List<double> presentQuad = new List<double>
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
    public double Exposure = 0.0;

    /// <summary>HDR クリア色 (背景)。</summary>
    public Color Background = Color.Rgb(0.09, 0.12, 0.15);

    /// <summary>SSAO (半解像度、depth 由来)。`radius` は view 空間。</summary>
    public Renderer3dSsao Ssao = new Renderer3dSsao();

    /// <summary>bloom。`threshold` は HDR 輝度、`strength` は合成量。</summary>
    public Renderer3dBloom Bloom = new Renderer3dBloom();

    /// <summary>ポスト AA (FXAA)。</summary>
    public Renderer3dAa Aa = new Renderer3dAa();

    /// <summary>8bit バンディング対策の triangular dither。</summary>
    public bool Dither = true;

    /// <summary>周辺減光 0..1 (0 = off)。</summary>
    public double Vignette = 0.0;

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
        var fov = cam.Fov ?? 60.0;
        var near = cam.Near ?? 0.1;
        var far = cam.Far ?? 100.0;
        Gfx.Size(out var w, out var h);
        var p = Mat4.PerspectiveLh(fov, (double)w / h, near, far);
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
        // Haxe 版の draws.resize(0) 相当。List.Clear() は tcs が
        // `(function() ... end)()` を emit し、直前の代入文と連結されて
        // 関数呼び出しに誤解釈される (Lua の文区切り曖昧性) ため使わない。
        draws = new List<Renderer3dDrawCmd>();
    }

    /// <summary>描画を記録する (実行は `End()`)。</summary>
    public void Draw(Mesh3d? mesh, Mat4 model, Draw3dOpts? opts = null)
    {
        if (mesh == null || !mesh.Ready())
            return;
        var tint = new List<double> { 1.0, 1.0, 1.0, 1.0 };
        var blend = Gfx.Blend.None;
        List<double>? bones = null;
        ShaderRef? shader = null;
        Dictionary<string, TextureRef>? textures = null;
        Dictionary<string, object>? uniforms = null;
        if (opts != null)
        {
            var t = opts.Tint;
            if (t != null)
                tint = new List<double> { t.R, t.G, t.B, t.A };
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
        var len = Math.Sqrt(Light.Dir.X * Light.Dir.X
            + Light.Dir.Y * Light.Dir.Y + Light.Dir.Z * Light.Dir.Z);
        var inv = len > 1e-6 ? 1.0 / len : 1.0;
        var dist = Shadow.Extent * 1.6;
        var leye = new Vec3(Shadow.Center.X + Light.Dir.X * inv * dist,
            Shadow.Center.Y + Light.Dir.Y * inv * dist,
            Shadow.Center.Z + Light.Dir.Z * inv * dist);
        // dir が真上のときの up 退避
        var up = Math.Abs(Light.Dir.Y) * inv > 0.99
            ? new Vec3(0, 0, 1)
            : new Vec3(0, 1, 0);
        var lview = Mat4.LookAtLh(leye, Shadow.Center, up);
        return Mat4.OrthoLh(Shadow.Extent * 2.0, Shadow.Extent * 2.0, 0.1,
            dist * 2.0) * lview;
    }

    // Haxe 版の Bones.pack(null, null) 相当。mesh が null なら resolve は
    // 呼ばれないが、Bones.pack の契約 (resolve 非 null) を保つためダミーを渡す。
    private static List<double> IdentityBones()
    {
        return Bones.Pack(null, (name, px, py, pz) => null);
    }

    private void ShadowPass(Mat4 lmvp, ShaderRef shStatic, ShaderRef shSkinned,
        TextureRef shadowMap)
    {
        Gfx.BeginPass(new PassOpts { DepthTarget = shadowMap, ClearDepth = 1.0 });
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
        Mat4 vp, Mat4 lmvp, double texel)
    {
        // (差し替え shader の追加 uniform は末尾でマージ)
        var u = new Dictionary<string, object>
        {
            ["mvp"] = (vp * d.Model).M,
            ["model"] = d.Model.M,
            ["light_mvp"] = lmvp.M,
            ["tint"] = d.Tint,
            ["light_dir"] = LightDirTable(),
            ["light_col"] = new List<double>
            {
                Light.Color.R * Light.Intensity,
                Light.Color.G * Light.Intensity,
                Light.Color.B * Light.Intensity,
                0.0,
            },
            ["sky_col"] = new List<double>
                { Sky.Top.R, Sky.Top.G, Sky.Top.B, Sky.Intensity },
            ["ground_col"] = new List<double>
                { Sky.Bottom.R, Sky.Bottom.G, Sky.Bottom.B, 0.0 },
            ["cam_pos"] = new List<double> { eye.X, eye.Y, eye.Z, 0.0 },
            ["shadow_p"] = new List<double>
                { texel, Shadow.Bias, Shadow.Enabled ? 1.0 : 0.0, 0.0 },
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

    private List<double> LightDirTable()
    {
        var len = Math.Sqrt(Light.Dir.X * Light.Dir.X
            + Light.Dir.Y * Light.Dir.Y + Light.Dir.Z * Light.Dir.Z);
        var inv = len > 1e-6 ? 1.0 / len : 1.0;
        return new List<double>
            { Light.Dir.X * inv, Light.Dir.Y * inv, Light.Dir.Z * inv, 0.0 };
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
    /// (Haxe 版の end。Lua キーワードのため End)。</summary>
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
        var texel = 1.0 / Shadow.Size;

        // forward pass (HDR)
        Gfx.BeginPass(new PassOpts
        {
            Target = hdr,
            DepthTarget = depth,
            // background も sRGB authoring → linear で HDR に置く
            ClearColor = new double[]
            {
                Math.Pow(Background.R, 2.2),
                Math.Pow(Background.G, 2.2),
                Math.Pow(Background.B, 2.2),
                1.0,
            },
            ClearDepth = 1.0,
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
        var projP = new List<double>
            { proj.M[0], Math.Abs(proj.M[5]), proj.M[10], proj.M[11] };

        // SSAO (半解像度)
        TextureRef? aoTex = null;
        if (Ssao.Enabled)
        {
            int aw = (int)Math.Floor(w / 2.0);
            int ah = (int)Math.Floor(h / 2.0);
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
                        ["ao_p"] = new List<double>
                            { Ssao.Radius, Ssao.Strength, 1.0 / aw, 1.0 / ah },
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
                bw = (int)Math.Floor(bw / 2.0);
                bh = (int)Math.Floor(bh / 2.0);
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
                        ["bl"] = new List<double> { Bloom.Threshold, 0.5, 0.0, 0.0 },
                    },
                });
                for (int li = 1; li < texs.Count; li++)
                {
                    Blit(texs[li], tentSh, new Dictionary<string, object>
                    {
                        ["scene"] = texs[li - 1],
                        ["uniforms"] = new Dictionary<string, object>
                        {
                            ["st"] = new List<double>
                                { 1.0 / ws[li - 1], 1.0 / hs[li - 1], 1.0, 0.0 },
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
                            ["st"] = new List<double>
                                { 1.0 / ws[j], 1.0 / hs[j], 0.7, 0.0 },
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
                ["en"] = new List<double>
                {
                    aoTex != null ? 1.0 : 0.0,
                    bloomTex != null ? Bloom.Strength : 0.0,
                    fogOn ? 1.0 : 0.0,
                    olOn ? 1.0 : 0.0,
                },
                ["fog_col"] = fog != null
                    ? new List<double>
                    {
                        Math.Pow(fog.Color.R, 2.2),
                        Math.Pow(fog.Color.G, 2.2),
                        Math.Pow(fog.Color.B, 2.2),
                        fog.Density,
                    }
                    : new List<double> { 0.0, 0.0, 0.0, 0.0 },
                ["ol"] = outline != null
                    ? new List<double>
                    {
                        Math.Pow(outline.Color.R, 2.2),
                        Math.Pow(outline.Color.G, 2.2),
                        Math.Pow(outline.Color.B, 2.2),
                        outline.Threshold,
                    }
                    : new List<double> { 0.0, 0.0, 0.0, 1.0 },
                ["px"] = new List<double> { 1.0 / w, 1.0 / h, 0.0, 0.0 },
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
                ["grade"] = new List<double>
                    { Exposure, Vignette, Dither ? 1.0 : 0.0, h },
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
                    ["px"] = new List<double> { 1.0 / w, 1.0 / h, 0.0, 0.0 },
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
