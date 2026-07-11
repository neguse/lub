struct FSIn {
  float3 normal : TEXCOORD2;
  float3 color : COLOR0;
};
[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float3 n = normalize(i.normal);
  float3 l = normalize(float3(-0.30, 0.85, 0.45));
  // half-lambert + 空色 ambient (屋外デーゲームの色)。
  // 観戦カメラはあらゆる向きから見るので、陰も潰れない明るめの照明にする
  float diff = dot(n, l) * 0.5 + 0.5;
  float3 sky = float3(0.42, 0.55, 0.72);
  float3 c = i.color * (0.30 + 0.70 * diff) + sky * (n.y * 0.5 + 0.5) * 0.16;
  return float4(c, 1.0);
}
