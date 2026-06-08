LUB_TEXTURE2D(scene);

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float2 o = float2(ddx(i.uv).x, 0.0);
  float3 c = LUB_SAMPLE_LOD(scene, i.uv).rgb * 0.227027;
  c += LUB_SAMPLE_LOD(scene, i.uv + o * 1.0).rgb * 0.1945946;
  c += LUB_SAMPLE_LOD(scene, i.uv - o * 1.0).rgb * 0.1945946;
  c += LUB_SAMPLE_LOD(scene, i.uv + o * 2.0).rgb * 0.1216216;
  c += LUB_SAMPLE_LOD(scene, i.uv - o * 2.0).rgb * 0.1216216;
  c += LUB_SAMPLE_LOD(scene, i.uv + o * 3.0).rgb * 0.054054;
  c += LUB_SAMPLE_LOD(scene, i.uv - o * 3.0).rgb * 0.054054;
  c += LUB_SAMPLE_LOD(scene, i.uv + o * 4.0).rgb * 0.016216;
  c += LUB_SAMPLE_LOD(scene, i.uv - o * 4.0).rgb * 0.016216;
  return float4(c, 1.0);
}
