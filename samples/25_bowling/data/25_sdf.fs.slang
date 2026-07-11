struct FSIn {
  float3 normal : TEXCOORD2;
  float3 color : COLOR0;
  float2 mr : TEXCOORD3;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float3 n = normalize(i.normal);
  float3 l = normalize(float3(-0.4, 1.0, -0.55));
  float metal = i.mr.x;
  float rough = i.mr.y;

  // 誘電体: half-lambert + 空色 ambient + roughness で絞る specular
  float diff = dot(n, l) * 0.5 + 0.5;
  float3 sky = float3(0.42, 0.48, 0.58);
  float3 dielectric = i.color * diff * diff + sky * (n.y * 0.5 + 0.5) * 0.18;
  float spec = pow(max(dot(n, l), 0.0), 32.0) * (1.0 - rough) * 0.5;
  dielectric += spec;

  // 金属: 環境マップの代わりに上下グラデ + 強い specular で金属感を出す
  float3 env =
      lerp(float3(0.18, 0.19, 0.23), float3(0.85, 0.90, 1.0), n.y * 0.5 + 0.5);
  float3 metallic = env * lerp(i.color, float3(1, 1, 1), 0.5);
  metallic += pow(max(dot(n, l), 0.0), 64.0) * (1.0 - rough) * 1.2;

  return float4(lerp(dielectric, metallic, metal), 1.0);
}
