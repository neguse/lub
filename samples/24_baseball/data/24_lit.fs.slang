struct FSIn {
  float3 normal : TEXCOORD1;
  float4 color : COLOR0;
};
[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float3 n = normalize(i.normal);
  float3 l = normalize(float3(-0.30, 0.85, 0.45));
  float diff = dot(n, l) * 0.5 + 0.5;
  float3 sky = float3(0.42, 0.55, 0.72);
  float3 c =
      i.color.rgb * (0.30 + 0.70 * diff) + sky * (n.y * 0.5 + 0.5) * 0.16;
  return float4(c, i.color.a);
}
