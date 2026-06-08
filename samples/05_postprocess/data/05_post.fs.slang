LUB_TEXTURE2D(scene);
struct FSIn {
  float2 uv : TEXCOORD0;
};
[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 c = LUB_SAMPLE(scene, i.uv);
  // Post-process: color invert + radial vignette so the difference vs. raw
  // offscreen output is visible.
  float3 inv = float3(1.0, 1.0, 1.0) - c.rgb;
  float2 d = i.uv - float2(0.5, 0.5);
  float vignette = 1.0 - saturate(dot(d, d) * 1.8);
  return float4(inv * vignette, 1.0);
}
