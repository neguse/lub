LUB_TEXTURE2D(scene);
LUB_TEXTURE2D(bloom);

static const float STRENGTH = 0.55;

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 base = LUB_SAMPLE_LOD(scene, i.uv);
  float3 b = LUB_SAMPLE_LOD(bloom, i.uv).rgb;
  return float4(base.rgb + b * STRENGTH, base.a);
}
