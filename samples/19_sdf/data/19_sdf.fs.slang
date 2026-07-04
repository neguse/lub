LUB_TEXTURE2D(matcap);
struct Uniforms {
  float4x4 mvp;
  float4x4 model;
  float4x4 view;
  float4 params; // x = metal override (0..1)
};
ConstantBuffer<Uniforms> u;
struct FSIn {
  float3 normal : TEXCOORD2;
  float3 vnormal : TEXCOORD3;
  float3 color : COLOR0;
  float2 mr : TEXCOORD4;
};
[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float3 n = normalize(i.normal);
  float3 l = normalize(float3(-0.45, 0.8, -0.5));
  // runtime override: メタル変身。頂点の材質を uniform で上書き補間する
  float metal = lerp(i.mr.x, 1.0, u.params.x);
  float rough = lerp(i.mr.y, 0.12, u.params.x);

  // 誘電体: half-lambert + 空色 ambient + roughness で絞る specular
  float diff = dot(n, l) * 0.5 + 0.5;
  float3 sky = float3(0.35, 0.45, 0.6);
  float3 dielectric = i.color * diff * diff + sky * (n.y * 0.5 + 0.5) * 0.22;
  float spec = pow(max(dot(n, l), 0.0), 32.0) * (1.0 - rough) * 0.5;
  dielectric += spec;

  // 金属: view-space normal で matcap (sphere map) を引き、albedo で着色
  float2 uv = normalize(i.vnormal).xy * float2(0.5, -0.5) + 0.5;
  float3 env = LUB_SAMPLE(matcap, uv).rgb;
  float3 tint =
      lerp(i.color, float3(1, 1, 1), 0.6); // 金属も albedo をうっすら残す
  float3 metallic = env * tint * (1.0 - rough * 0.5);

  return float4(lerp(dielectric, metallic, metal), 1.0);
}
