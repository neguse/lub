package lubx;

import lub.Gfx;
import lub.Math;
import lubx.Mesh3d;

/** `Renderer3d.draw()` の per-draw オプション。 **/
typedef Draw3dOpts = {
	/** 頂点色に乗じる色 (省略時白)。a < 1 でも自動では blend にならない。 **/
	?tint:Color,
	/** `Gfx.ALPHA` 等。指定すると opaque 群の後に描かれ、影を落とさない。 **/
	?blend:Int,
	/** skinned メッシュ用。`Bones.pack()` の 128 float。 **/
	?bones:lua.Table<Int, Float>,
	/** material 差し替え。頂点レイアウトと uniform 名は既定 shader と同じ契約
		(必要な uniform 名だけ宣言すればよい)。 **/
	?shader:Dynamic,
	/** 差し替え shader 用の追加テクスチャ (名前 → TextureRef)。 **/
	?textures:Dynamic,
	/** 差し替え shader 用の追加 uniform (名前 → Table)。既定名と衝突したら上書き。 **/
	?uniforms:Dynamic,
}

/** `Renderer3d.begin()` のカメラ。`Camera3d.vp` と同じ形。 **/
typedef Camera = {
	eye:Vec3,
	target:Vec3,
	?up:Vec3,
	/** 度。省略時 60。 **/
	?fov:Float,
	?near:Float,
	?far:Float,
}

private typedef DrawCmd = {
	mesh:Mesh3d,
	model:Mat4,
	tint:Array<Float>,
	blend:Int,
	bones:lua.Table<Int, Float>,
	shader:Dynamic,
	textures:Dynamic,
	uniforms:Dynamic,
}

/**
	forward + HDR の組み込みレンダラ。「メッシュを投げたら一発でいい絵」が目標。

	```haxe
	var ren = new Renderer3d("main");
	// 毎フレーム:
	ren.begin({eye: eye, target: tgt, fov: 38});
	ren.draw(mesh, model);
	ren.draw(charMesh, m2, {bones: packed});
	ren.end(); // shadow → forward(HDR) → tonemap → swapchain
	```

	既定で ON: 平行光源 + hemispheric ambient、shadow (PCF 3×3)、AgX tonemap。
	作風は効果別のフィールドで調整する (`light` / `sky` / `shadow` / `exposure`)。
	offscreen と swapchain の y 反転差はここが吸収するので、利用側は気にしない。

	`end()` 後の swapchain には `Gfx.beginPass({target: Gfx.mainTex, load: Gfx.LOAD})`
	で UI やテキストを重ね描きできる。
**/
class Renderer3d {
	// --- 埋め込み shader (pncm / pncmw 頂点レイアウト契約) -------------------
	// NOTE: slang の WGSL 出力は TEXCOORDn を @location(n) に割り当てるので、
	// TEXCOORD の番号は宣言位置に合わせる (ズレると wasm で attr が崩れる)。
	static inline var LIT_VS_COMMON:String = "
struct Uniforms {
  float4x4 mvp;
  float4x4 model;
  float4x4 light_mvp;
  float4 tint;
";

	static inline var LIT_VS_BODY:String = "
struct VSOut {
  float3 wn : TEXCOORD0;
  float3 wp : TEXCOORD1;
  float4 lpos : TEXCOORD2;
  float2 mr : TEXCOORD3;
  float4 albedo : COLOR0;
  float4 pos : SV_Position;
};
";

	static var LIT_STATIC_VS:String = LIT_VS_COMMON
		+ "};
ConstantBuffer<Uniforms> u;
struct VSIn {
  float3 pos : POSITION;
  float3 normal : NORMAL;
  float3 color : COLOR;
  float2 mr : TEXCOORD3;
};"
		+ LIT_VS_BODY
		+ "
[shader(\"vertex\")] VSOut vs_main(VSIn i) {
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
";

	static var LIT_SKINNED_VS:String = LIT_VS_COMMON
		+ "  float4x4 bones[8];
};
ConstantBuffer<Uniforms> u;
struct VSIn {
  float3 pos : POSITION;
  float3 normal : NORMAL;
  float3 color : COLOR;
  float2 mr : TEXCOORD3;
  float4 skin : TEXCOORD4; // j0, w0, j1, w1
};"
		+ LIT_VS_BODY
		+ "
[shader(\"vertex\")] VSOut vs_main(VSIn i) {
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
";

	// 誘電体/金属の分岐は 23_crane_game 由来。光方向・環境光を uniform 化し、
	// 平行光成分に shadow を掛ける。出力は HDR (クランプしない)。
	static var LIT_FS:String = "
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

[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {
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
";

	static var SHADOW_STATIC_VS:String = "
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
[shader(\"vertex\")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = mul(u.light_mvp, mul(u.model, float4(i.pos, 1.0)));
  return o;
}
";

	static var SHADOW_SKINNED_VS:String = "
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
[shader(\"vertex\")] VSOut vs_main(VSIn i) {
  VSOut o;
  int j0 = int(i.skin.x);
  int j1 = int(i.skin.z);
  float4 p4 = float4(i.pos, 1.0);
  float3 sp =
      (mul(u.bones[j0], p4) * i.skin.y + mul(u.bones[j1], p4) * i.skin.w).xyz;
  o.pos = mul(u.light_mvp, mul(u.model, float4(sp, 1.0)));
  return o;
}
";

	// depth-only pass: color attachment が無いので出力は捨てられる
	// (webgpu は fragment stage 自体が省かれる)。
	static var SHADOW_FS:String = "
[shader(\"fragment\")] float4 fs_main() : SV_Target {
  return float4(0.0, 0.0, 0.0, 1.0);
}
";

	// 全 offscreen ポストパス共通の flip quad (uv が source texture と同向)。
	static var FLIP_QUAD:Array<Float> = [
		-1, -1, 0, 1,
		 1, -1, 1, 1,
		 1,  1, 1, 0,
		-1, -1, 0, 1,
		 1,  1, 1, 0,
		-1,  1, 0, 0,
	];

	// SSAO: depth から view 位置を復元し、面法線は depth 微分から。半解像度。
	// カーネルは固定 12 サンプルの渦巻き (乱数なし = 決定的)。
	static var SSAO_FS:String = "
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

[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {
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
";

	// bloom 抽出: soft-knee threshold。
	static var BRIGHT_FS:String = "
LUB_TEXTURE2D(scene);
struct FsU {
  float4 bl; // x = threshold, y = knee
};
ConstantBuffer<FsU> f;
struct FSIn {
  float2 uv : TEXCOORD0;
};
[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {
  float3 c = LUB_SAMPLE_LOD(scene, i.uv).rgb;
  float lum = max(c.r, max(c.g, c.b));
  float knee = f.bl.y;
  float soft = saturate(lum - f.bl.x + knee) ;
  soft = soft * soft / (4.0 * max(knee, 1e-4));
  float w = max(soft, lum - f.bl.x) / max(lum, 1e-4);
  return float4(c * saturate(w), 1.0);
}
";

	// 縮小/拡大 (LINEAR サンプラ + 4 tap tent)。up は ADDITIVE blend で描く。
	static var BLIT_TENT_FS:String = "
LUB_TEXTURE2D(scene);
struct FsU {
  float4 st; // x = 1/srcW, y = 1/srcH, z = gain
};
ConstantBuffer<FsU> f;
struct FSIn {
  float2 uv : TEXCOORD0;
};
[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {
  float2 t = f.st.xy;
  float3 c = LUB_SAMPLE_LOD(scene, i.uv + float2(-t.x, -t.y)).rgb;
  c += LUB_SAMPLE_LOD(scene, i.uv + float2(t.x, -t.y)).rgb;
  c += LUB_SAMPLE_LOD(scene, i.uv + float2(-t.x, t.y)).rgb;
  c += LUB_SAMPLE_LOD(scene, i.uv + float2(t.x, t.y)).rgb;
  return float4(c * 0.25 * f.st.z, 1.0);
}
";

	// composite: scene * AO + bloom、fog、outline (どちらも opt-in、HDR 空間)。
	static var COMPOSITE_FS:String = "
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

[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {
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
";

	// FXAA (console 風の簡易版)。LDR に対して。
	static var FXAA_FS:String = "
LUB_TEXTURE2D(scene);
struct FsU {
  float4 px; // x = 1/w, y = 1/h
};
ConstantBuffer<FsU> f;
struct FSIn {
  float2 uv : TEXCOORD0;
};
float luma(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {
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
";

	// 素の blit (FXAA off 時の present)。
	static var PRESENT_FS:String = "
LUB_TEXTURE2D(scene);
struct FSIn {
  float2 uv : TEXCOORD0;
};
[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {
  return float4(LUB_SAMPLE_LOD(scene, i.uv).rgb, 1.0);
}
";

	static var QUAD_VS:String = "
struct VSIn {
  float2 pos : POSITION;
  float2 uv : TEXCOORD0;
};
struct VSOut {
  float2 uv : TEXCOORD0;
  float4 pos : SV_Position;
};
[shader(\"vertex\")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = float4(i.pos, 0.0, 1.0);
  o.uv = i.uv;
  return o;
}
";

	// AgX (minimal fit)。HDR → display。exposure は stop (2^n)。
	static var TONEMAP_FS:String = "
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

[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {
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
";

	// swapchain 向け present quad (clip y = -1 → uv.y = 0)。offscreen 側は
	// proj.m[5] 反転で screen 向きに描かれているので、この 1 枚で向きが合う。
	static var PRESENT_QUAD:Array<Float> = [
		-1, -1, 0, 0,
		 1, -1, 1, 0,
		 1,  1, 1, 1,
		-1, -1, 0, 0,
		 1,  1, 1, 1,
		-1,  1, 0, 1,
	];

	// --- 公開オプション -------------------------------------------------------

	/** 平行光源。`dir` は光へ向かうベクトル (正規化不要)。 **/
	public var light = {
		dir: new Vec3(-0.4, 1.0, -0.55),
		color: Color.rgb(1.0, 0.96, 0.9),
		intensity: 1.25,
	};

	/** hemispheric ambient の空色 (上) / 地面色 (下) と強度。 **/
	public var sky = {
		top: Color.rgb(0.42, 0.48, 0.58),
		bottom: Color.rgb(0.20, 0.18, 0.16),
		intensity: 0.55,
	};

	/** shadow map。`center`/`extent` は光のオルソ範囲 (world)。 **/
	public var shadow = {
		enabled: true,
		size: 2048,
		center: new Vec3(0, 0, 0),
		extent: 12.0,
		bias: 0.004,
	};

	/** 露出 (stop)。+1 で 2 倍明るい。 **/
	public var exposure:Float = 0.0;

	/** HDR クリア色 (背景)。 **/
	public var background:Color = Color.rgb(0.09, 0.12, 0.15);

	/** SSAO (半解像度、depth 由来)。`radius` は view 空間。 **/
	public var ssao = {enabled: true, radius: 0.6, strength: 0.85};

	/** bloom。`threshold` は HDR 輝度、`strength` は合成量。 **/
	public var bloom = {enabled: true, threshold: 1.0, strength: 0.35};

	/** ポスト AA (FXAA)。 **/
	public var aa = {enabled: true};

	/** 8bit バンディング対策の triangular dither。 **/
	public var dither:Bool = true;

	/** 周辺減光 0..1 (0 = off)。 **/
	public var vignette:Float = 0.0;

	/** 距離 fog (opt-in)。`density` は 1/距離スケール。 **/
	public var fog:Null<{color:Color, density:Float}> = null;

	/** depth エッジの輪郭線 (opt-in)。`threshold` は view 距離差。 **/
	public var outline:Null<{color:Color, threshold:Float}> = null;

	/** 中間バッファの確認用: "ao" / "bloom" / "hdr" を swapchain に直接出す。 **/
	public var debugView:Null<String> = null;

	/** world → clip (y-flip なし)。スクリーン座標への投影 (HUD 追従等) 用。 **/
	public var viewProj(default, null):Mat4 = null;

	/** view 行列 (begin() で確定)。差し替え shader の view-space 計算用。 **/
	public var viewMat(default, null):Mat4 = null;

	final key:String;
	var draws:Array<DrawCmd> = [];
	var view:Mat4 = null;
	var proj:Mat4 = null;
	var vp:Mat4 = null;
	var eye:Vec3 = new Vec3(0, 0, 0);

	public function new(key:String) {
		this.key = key;
	}

	/** Phys3d の pose (x,y,z,qx,qy,qz,qw) → model 行列。 **/
	public static function poseMat(pose:Dynamic):Mat4 {
		return Mat4.translate(new Vec3(pose.x, pose.y, pose.z)) * new Quat(pose.qx, pose.qy, pose.qz, pose.qw).toMat4();
	}

	/** フレーム開始。カメラを確定し draw 列を空にする。 **/
	public function begin(cam:Camera):Void {
		var up = cam.up != null ? cam.up : new Vec3(0, 1, 0);
		var fov = cam.fov != null ? cam.fov : 60.0;
		var near = cam.near != null ? cam.near : 0.1;
		var far = cam.far != null ? cam.far : 100.0;
		var sz = Gfx.size();
		proj = Mat4.perspectiveLh(fov, sz.w / sz.h, near, far);
		view = Mat4.lookAtLh(cam.eye, cam.target, up);
		viewMat = view;
		viewProj = proj * view;
		// offscreen target は swapchain と違い y-flip されないので、clip y を
		// あらかじめ反転して screen 向きで描く (present quad と対)。
		proj.m[5] = -proj.m[5];
		vp = proj * view;
		eye = cam.eye;
		draws.resize(0);
	}

	/** 描画を記録する (実行は `end()`)。 **/
	public function draw(mesh:Mesh3d, model:Mat4, ?opts:Draw3dOpts):Void {
		if (mesh == null || !mesh.ready())
			return;
		var tint = [1.0, 1.0, 1.0, 1.0];
		var blend = Gfx.NONE;
		var bones:lua.Table<Int, Float> = null;
		var shader:Dynamic = null;
		var textures:Dynamic = null;
		var uniforms:Dynamic = null;
		if (opts != null) {
			if (opts.tint != null)
				tint = [opts.tint.r, opts.tint.g, opts.tint.b, opts.tint.a];
			if (opts.blend != null)
				blend = opts.blend;
			if (opts.bones != null)
				bones = opts.bones;
			if (opts.shader != null)
				shader = opts.shader;
			if (opts.textures != null)
				textures = opts.textures;
			if (opts.uniforms != null)
				uniforms = opts.uniforms;
		}
		draws.push({
			mesh: mesh,
			model: model,
			tint: tint,
			blend: blend,
			bones: bones,
			shader: shader,
			textures: textures,
			uniforms: uniforms,
		});
	}

	function lightMvp():Mat4 {
		var len = Math.sqrt(light.dir.x * light.dir.x + light.dir.y * light.dir.y + light.dir.z * light.dir.z);
		var inv = len > 1e-6 ? 1.0 / len : 1.0;
		var dist = shadow.extent * 1.6;
		var leye = new Vec3(shadow.center.x + light.dir.x * inv * dist, shadow.center.y + light.dir.y * inv * dist, shadow.center.z + light.dir.z * inv * dist);
		// dir が真上のときの up 退避
		var up = Math.abs(light.dir.y) * inv > 0.99 ? new Vec3(0, 0, 1) : new Vec3(0, 1, 0);
		var lview = Mat4.lookAtLh(leye, shadow.center, up);
		return Mat4.orthoLh(shadow.extent * 2.0, shadow.extent * 2.0, 0.1, dist * 2.0) * lview;
	}

	function shadowPass(lmvp:Mat4, shStatic:Dynamic, shSkinned:Dynamic, shadowMap:Dynamic, shadowSize:Int):Void {
		Gfx.beginPass({depth_target: shadowMap, clear_depth: 1.0});
		var lm = lua.Table.fromArray(lmvp.m);
		for (d in draws) {
			if (d.blend != Gfx.NONE)
				continue; // 半透明は影を落とさない
			var u:Dynamic = {
				light_mvp: lm,
				model: lua.Table.fromArray(d.model.m),
			};
			if (d.mesh.skinned)
				u.bones = d.bones != null ? d.bones : Bones.pack(null, null);
			Gfx.draw(d.mesh.indexCount, {
				verts: d.mesh.vb,
				indices: d.mesh.ib,
				uniforms: u,
			}, {
				shader: d.mesh.skinned ? shSkinned : shStatic,
				depth: true,
				depth_write: true,
				cull: Gfx.NONE,
			});
		}
		Gfx.endPass();
	}

	function litUniforms(d:DrawCmd, lmvp:Mat4, texel:Float):Dynamic {
		// (差し替え shader の追加 uniform は末尾でマージ)
		var u:Dynamic = {
			mvp: lua.Table.fromArray((vp * d.model).m),
			model: lua.Table.fromArray(d.model.m),
			light_mvp: lua.Table.fromArray(lmvp.m),
			tint: lua.Table.fromArray(d.tint),
			light_dir: lightDirTable(),
			light_col: lua.Table.fromArray([
				light.color.r * light.intensity,
				light.color.g * light.intensity,
				light.color.b * light.intensity,
				0.0
			]),
			sky_col: lua.Table.fromArray([sky.top.r, sky.top.g, sky.top.b, sky.intensity]),
			ground_col: lua.Table.fromArray([sky.bottom.r, sky.bottom.g, sky.bottom.b, 0.0]),
			cam_pos: lua.Table.fromArray([eye.x, eye.y, eye.z, 0.0]),
			shadow_p: lua.Table.fromArray([texel, shadow.bias, shadow.enabled ? 1.0 : 0.0, 0.0]),
		};
		if (d.mesh.skinned)
			u.bones = d.bones != null ? d.bones : Bones.pack(null, null);
		if (d.uniforms != null) {
			for (name in Reflect.fields(d.uniforms))
				Reflect.setField(u, name, Reflect.field(d.uniforms, name));
		}
		return u;
	}

	function lightDirTable():lua.Table<Int, Float> {
		var len = Math.sqrt(light.dir.x * light.dir.x + light.dir.y * light.dir.y + light.dir.z * light.dir.z);
		var inv = len > 1e-6 ? 1.0 / len : 1.0;
		return lua.Table.fromArray([light.dir.x * inv, light.dir.y * inv, light.dir.z * inv, 0.0]);
	}

	// flip quad で target 全面に 1 パス描く。
	function blit(target:Dynamic, shader:Dynamic, bindings:Dynamic, ?load:Int, ?blend:Int):Void {
		var opts:Dynamic = {target: target};
		if (load != null)
			opts.load = load;
		Gfx.beginPass(opts);
		bindings.verts = flipQuadBuf;
		Gfx.draw(6, bindings, {
			shader: shader,
			depth: false,
			cull: Gfx.NONE,
			blend: blend != null ? blend : Gfx.NONE,
		});
		Gfx.endPass();
	}

	var flipQuadBuf:Dynamic = null;

	/** 記録した draw 列を実行して swapchain まで出す。 **/
	public function end():Void {
		if (vp == null)
			return;
		var sz = Gfx.size();
		var rtVer = sz.w * 65536 + sz.h;

		var litStatic = Gfx.useShader(key + "_lit_s", LIT_STATIC_VS, LIT_FS, 1);
		var litSkinned = Gfx.useShader(key + "_lit_k", LIT_SKINNED_VS, LIT_FS, 1);
		var shStatic = Gfx.useShader(key + "_sh_s", SHADOW_STATIC_VS, SHADOW_FS, 1);
		var shSkinned = Gfx.useShader(key + "_sh_k", SHADOW_SKINNED_VS, SHADOW_FS, 1);
		var tonemap = Gfx.useShader(key + "_tm", QUAD_VS, TONEMAP_FS, 1);
		var ssaoSh = Gfx.useShader(key + "_ssao", QUAD_VS, SSAO_FS, 1);
		var brightSh = Gfx.useShader(key + "_br", QUAD_VS, BRIGHT_FS, 1);
		var tentSh = Gfx.useShader(key + "_tent", QUAD_VS, BLIT_TENT_FS, 1);
		var compSh = Gfx.useShader(key + "_comp", QUAD_VS, COMPOSITE_FS, 1);
		var fxaaSh = Gfx.useShader(key + "_fxaa", QUAD_VS, FXAA_FS, 1);
		var presentSh = Gfx.useShader(key + "_pr", QUAD_VS, PRESENT_FS, 1);
		if (litStatic == null || litSkinned == null || shStatic == null || shSkinned == null || tonemap == null || ssaoSh == null || brightSh == null
			|| tentSh == null || compSh == null || fxaaSh == null || presentSh == null)
			return;

		var hdr = Gfx.useTexture(key + "_hdr", sz.w, sz.h, Gfx.RGBA16F, null, rtVer, {target: true, filter: Gfx.LINEAR, wrap: Gfx.CLAMP});
		var depth = Gfx.useTexture(key + "_depth", sz.w, sz.h, Gfx.DEPTH32F, null, rtVer, {target: true, wrap: Gfx.CLAMP});
		var shadowMap = Gfx.useTexture(key + "_sm", shadow.size, shadow.size, Gfx.DEPTH32F, null, shadow.size, {target: true, wrap: Gfx.CLAMP});
		var quad = Gfx.useBuffer(key + "_quad", Gfx.VERTEX, lua.Table.fromArray(PRESENT_QUAD), 1);
		flipQuadBuf = Gfx.useBuffer(key + "_fquad", Gfx.VERTEX, lua.Table.fromArray(FLIP_QUAD), 1);

		var lmvp = lightMvp();
		if (shadow.enabled)
			shadowPass(lmvp, shStatic, shSkinned, shadowMap, shadow.size);
		var texel = 1.0 / shadow.size;

		// forward pass (HDR)
		Gfx.beginPass({
			target: hdr,
			depth_target: depth,
			// background も sRGB authoring → linear で HDR に置く
			clear_color: lua.Table.fromArray([
				Math.pow(background.r, 2.2),
				Math.pow(background.g, 2.2),
				Math.pow(background.b, 2.2),
				1.0
			]),
			clear_depth: 1.0,
		});
		// opaque → blend の順
		for (phase in 0...2) {
			for (d in draws) {
				var isBlend = d.blend != Gfx.NONE;
				if ((phase == 0) == isBlend)
					continue;
				var shader = d.shader != null ? d.shader : (d.mesh.skinned ? litSkinned : litStatic);
				var bindings:Dynamic = {
					verts: d.mesh.vb,
					indices: d.mesh.ib,
					shadow_map: shadowMap,
					uniforms: litUniforms(d, lmvp, texel),
				};
				if (d.textures != null) {
					for (name in Reflect.fields(d.textures))
						Reflect.setField(bindings, name, Reflect.field(d.textures, name));
				}
				Gfx.draw(d.mesh.indexCount, bindings, {
					shader: shader,
					depth: true,
					depth_write: !isBlend,
					cull: Gfx.NONE,
					blend: d.blend,
				});
			}
		}
		Gfx.endPass();

		// proj は m[5] を反転済みなので |m5| を渡す
		var projP = lua.Table.fromArray([proj.m[0], Math.abs(proj.m[5]), proj.m[10], proj.m[11]]);

		// SSAO (半解像度)
		var aoTex:Dynamic = null;
		if (ssao.enabled) {
			var aw = sz.w >> 1;
			var ah = sz.h >> 1;
			aoTex = Gfx.useTexture(key + "_ao", aw, ah, Gfx.R8, null, rtVer, {target: true, filter: Gfx.LINEAR, wrap: Gfx.CLAMP});
			blit(aoTex, ssaoSh, {
				depth_tex: depth,
				uniforms: {
					pp: projP,
					ao_p: lua.Table.fromArray([ssao.radius, ssao.strength, 1.0 / aw, 1.0 / ah]),
				},
			});
		}

		// bloom: bright → 縮小 3 段 → ADDITIVE で逆順に戻す
		var bloomTex:Dynamic = null;
		if (bloom.enabled) {
			var levels = 4;
			var texs:Array<Dynamic> = [];
			var ws:Array<Int> = [];
			var hs:Array<Int> = [];
			var w = sz.w;
			var h = sz.h;
			for (li in 0...levels) {
				w = w >> 1;
				h = h >> 1;
				if (w < 8 || h < 8) {
					levels = li;
					break;
				}
				ws.push(w);
				hs.push(h);
				texs.push(Gfx.useTexture(key + "_bl" + li, w, h, Gfx.RGBA16F, null, rtVer, {target: true, filter: Gfx.LINEAR, wrap: Gfx.CLAMP}));
			}
			if (texs.length > 0) {
				blit(texs[0], brightSh, {
					scene: hdr,
					uniforms: {bl: lua.Table.fromArray([bloom.threshold, 0.5, 0.0, 0.0])},
				});
				for (li in 1...texs.length)
					blit(texs[li], tentSh, {
						scene: texs[li - 1],
						uniforms: {st: lua.Table.fromArray([1.0 / ws[li - 1], 1.0 / hs[li - 1], 1.0, 0.0])},
					});
				var li = texs.length - 1;
				while (li > 0) {
					blit(texs[li - 1], tentSh, {
						scene: texs[li],
						uniforms: {st: lua.Table.fromArray([1.0 / ws[li], 1.0 / hs[li], 0.7, 0.0])},
					}, Gfx.LOAD, Gfx.ADDITIVE);
					li--;
				}
				bloomTex = texs[0];
			}
		}

		// composite (AO 乗算 + bloom 加算 + fog / outline)
		var post = Gfx.useTexture(key + "_post", sz.w, sz.h, Gfx.RGBA16F, null, rtVer, {target: true, filter: Gfx.LINEAR, wrap: Gfx.CLAMP});
		var fogOn = fog != null;
		var olOn = outline != null;
		blit(post, compSh, {
			scene: hdr,
			ao_tex: aoTex != null ? aoTex : hdr,
			bloom_tex: bloomTex != null ? bloomTex : hdr,
			depth_tex: depth,
			uniforms: {
				pp: projP,
				en: lua.Table.fromArray([
					aoTex != null ? 1.0 : 0.0,
					bloomTex != null ? bloom.strength : 0.0,
					fogOn ? 1.0 : 0.0,
					olOn ? 1.0 : 0.0
				]),
				fog_col: lua.Table.fromArray(fogOn ? [
					Math.pow(fog.color.r, 2.2),
					Math.pow(fog.color.g, 2.2),
					Math.pow(fog.color.b, 2.2),
					fog.density
				] : [0.0, 0.0, 0.0, 0.0]),
				ol: lua.Table.fromArray(olOn ? [
					Math.pow(outline.color.r, 2.2),
					Math.pow(outline.color.g, 2.2),
					Math.pow(outline.color.b, 2.2),
					outline.threshold
				] : [0.0, 0.0, 0.0, 1.0]),
				px: lua.Table.fromArray([1.0 / sz.w, 1.0 / sz.h, 0.0, 0.0]),
			},
		});

		// tonemap (+vignette +dither) → LDR
		var ldr = Gfx.useTexture(key + "_ldr", sz.w, sz.h, Gfx.RGBA8, null, rtVer, {target: true, filter: Gfx.LINEAR, wrap: Gfx.CLAMP});
		blit(ldr, tonemap, {
			scene: post,
			uniforms: {grade: lua.Table.fromArray([exposure, vignette, dither ? 1.0 : 0.0, sz.h])},
		});

		// FXAA (or 素通し) → swapchain
		if (debugView != null) {
			var dbg:Dynamic = switch (debugView) {
				case "ao": aoTex;
				case "bloom": bloomTex;
				case "hdr": hdr;
				case _: null;
			}
			if (dbg != null) {
				Gfx.beginPass({target: Gfx.mainTex});
				Gfx.draw(6, {verts: quad, scene: dbg}, {shader: presentSh, depth: false, cull: Gfx.NONE});
				Gfx.endPass();
				return;
			}
		}
		Gfx.beginPass({target: Gfx.mainTex});
		if (aa.enabled) {
			Gfx.draw(6, {
				verts: quad,
				scene: ldr,
				uniforms: {px: lua.Table.fromArray([1.0 / sz.w, 1.0 / sz.h, 0.0, 0.0])},
			}, {shader: fxaaSh, depth: false, cull: Gfx.NONE});
		} else {
			Gfx.draw(6, {verts: quad, scene: ldr}, {shader: presentSh, depth: false, cull: Gfx.NONE});
		}
		Gfx.endPass();
	}
}
