LUB_TEXTURE2D(tex);

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return float4(LUB_SAMPLE(tex, float2(0.5, 0.5)).rgb, 1.0);
}
