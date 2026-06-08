// Additive bloom composite: base + blurred bright buffer * STRENGTH. No uniform
// block (see 12_fog.fs).
LUB_TEXTURE2D(scene); // base beauty
LUB_TEXTURE2D(bloom); // blurred bright-pass

static const float STRENGTH = 0.9;

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float3 b = LUB_SAMPLE_LOD(scene, i.uv).rgb;
  float3 bl = LUB_SAMPLE_LOD(bloom, i.uv).rgb;
  return float4(b + bl * STRENGTH, 1.0);
}
