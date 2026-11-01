LUB_TEXTURE2D(tex);

struct FSIn {
  float2 tint : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return float4(LUB_SAMPLE(tex, float2(0.5, 0.5)).r, i.tint, 1.0);
}
