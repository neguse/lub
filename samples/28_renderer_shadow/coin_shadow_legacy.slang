// 調査開始時の影。比較サンプル専用で、本体の描画には使わない。

LUB_TEXTURE2D(shadow_map);
struct FsU {
  float4 light_dir;  // world, toward light (normalized)
  float4 light_col;  // rgb * intensity
  float4 sky_col;    // hemispheric ambient (上), w = ambient 強度
  float4 ground_col; // hemispheric ambient (下)
  float4 cam_pos;    // world camera (specular 用)
  float4 shadow_p;   // x = 1/texsize, y = bias, z = enabled
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
  if (uv.x < 0.0f || uv.x > 1.0f || uv.y < 0.0f || uv.y > 1.0f ||
      ndc.z < 0.0f || ndc.z > 1.0f)
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
  float ndl = dot(n, l);
  // 裏向きの面に直接光を足すと、shadow bias が明るい縁として見える。
  // 拡散・鏡面とも光側だけに当て、裏側は環境光で照らす。
  float sh = ndl > 0.0f ? shadow_factor(i.lpos, ndl) : 0.0f;
  float up = n.y * 0.5f + 0.5f;
  float3 hemi = lerp(f.ground_col.rgb, f.sky_col.rgb, up) * f.sky_col.w;
  float3 v = normalize(f.cam_pos.xyz - i.wp);
  float3 hv = normalize(l + v);

  // 誘電体: Lambert + hemispheric ambient + roughness で絞る specular
  float diff = saturate(ndl);
  float3 direct = f.light_col.rgb * diff * sh;
  float spec = pow(max(dot(n, hv), 0.0f), 32.0f) * (1.0f - rough) * 0.5f * sh;
  float3 dielectric = i.albedo.rgb * (direct + hemi) + f.light_col.rgb * spec;

  // 金属: 上下グラデ環境 + 強い specular
  float3 env = lerp(f.ground_col.rgb * 0.8f, f.sky_col.rgb * 1.6f, up);
  float3 metallic = env * lerp(i.albedo.rgb, float3(1.0f, 1.0f, 1.0f), 0.5f);
  metallic += f.light_col.rgb * pow(max(dot(n, hv), 0.0f), 64.0f) *
              (1.0f - rough) * 1.2f * sh;

  return float4(lerp(dielectric, metallic, metal), i.albedo.a);
}
