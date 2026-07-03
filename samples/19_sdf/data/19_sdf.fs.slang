struct FSIn {
  float3 normal : TEXCOORD0;
};
[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float3 n = normalize(i.normal);
  float3 l = normalize(float3(-0.45, 0.8, -0.5));
  // half-lambert + 空色の ambient。SDF 勾配由来の法線の滑らかさが見える。
  float diff = dot(n, l) * 0.5 + 0.5;
  float3 base = float3(0.93, 0.55, 0.32);
  float3 sky = float3(0.35, 0.45, 0.6);
  float3 c = base * diff * diff + sky * (n.y * 0.5 + 0.5) * 0.22;
  return float4(c, 1.0);
}
