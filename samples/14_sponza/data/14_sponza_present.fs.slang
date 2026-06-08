LUB_TEXTURE2D(scene);

struct FSIn {
  float2 uv : TEXCOORD0;
};

float3 linear_to_srgb(float3 c) { return pow(max(c, 0.0), float3(1.0 / 2.2)); }

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 src = LUB_SAMPLE_LOD(scene, i.uv);
  float3 c = max(src.rgb, 0.0);
  if (src.a > 0.5) {
    c = c * 1.08;
    c = c / (c + 1.0);
  }
  return float4(linear_to_srgb(c), 1.0);
}
