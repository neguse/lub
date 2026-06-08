LUB_TEXTURE2D(scene);
LUB_TEXTURE2D(gpos);

static const float FOG_START = 7.0;
static const float FOG_END = 36.0;
static const float3 FOG_COLOR = float3(0.030, 0.036, 0.046);

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 src = LUB_SAMPLE_LOD(scene, i.uv);
  float d = LUB_SAMPLE_LOD(gpos, i.uv).z;
  if (d <= 0.0001)
    return src;
  float f = saturate((d - FOG_START) / (FOG_END - FOG_START));
  return float4(lerp(src.rgb, FOG_COLOR, f), src.a);
}
