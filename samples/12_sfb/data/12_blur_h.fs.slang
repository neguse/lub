// Separable 9-tap Gaussian blur, horizontal. Texel size baked for a 1280x720
// target (see 12_fog.fs for why there is no uniform block).
LUB_TEXTURE2D(scene);

struct FSIn {
  float2 uv : TEXCOORD0;
};

float3 blur9(float2 uv, float2 o) {
  float w0 = 0.227027, w1 = 0.1945946, w2 = 0.1216216, w3 = 0.054054,
        w4 = 0.016216;
  float3 c = LUB_SAMPLE_LOD(scene, uv).rgb * w0;
  c += LUB_SAMPLE_LOD(scene, uv + o * 1.0).rgb * w1;
  c += LUB_SAMPLE_LOD(scene, uv - o * 1.0).rgb * w1;
  c += LUB_SAMPLE_LOD(scene, uv + o * 2.0).rgb * w2;
  c += LUB_SAMPLE_LOD(scene, uv - o * 2.0).rgb * w2;
  c += LUB_SAMPLE_LOD(scene, uv + o * 3.0).rgb * w3;
  c += LUB_SAMPLE_LOD(scene, uv - o * 3.0).rgb * w3;
  c += LUB_SAMPLE_LOD(scene, uv + o * 4.0).rgb * w4;
  c += LUB_SAMPLE_LOD(scene, uv - o * 4.0).rgb * w4;
  return c;
}

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  // texel from screen-space derivative of uv -> auto-adapts to the RT size.
  float2 step = float2(ddx(i.uv).x, 0.0);
  return float4(blur9(i.uv, step), 1.0);
}
