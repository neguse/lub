struct FSIn {
  float3 normal : TEXCOORD2;
  float3 color : COLOR0;
};
[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float3 n = normalize(i.normal);
  float3 l = normalize(float3(-0.4, 1.0, -0.55));
  // half-lambert + 空色 ambient。だるまの丸みが出るよう柔らかめに
  float diff = dot(n, l) * 0.5 + 0.5;
  float3 sky = float3(0.35, 0.45, 0.6);
  float3 c = i.color * diff * diff + sky * (n.y * 0.5 + 0.5) * 0.20;
  c += pow(max(dot(n, l), 0.0), 24.0) * 0.25;
  return float4(c, 1.0);
}
