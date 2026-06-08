LUB_TEXTURE2D(scene);
LUB_TEXTURE2D(blurred);
LUB_TEXTURE2D(gpos);

static const float FOCUS = 9.0;
static const float RANGE = 18.0;
static const float MAX_COC = 0.45;

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 sharp = LUB_SAMPLE_LOD(scene, i.uv);
  float d = LUB_SAMPLE_LOD(gpos, i.uv).z;
  if (d <= 0.0001)
    return sharp;
  float3 blur = LUB_SAMPLE_LOD(blurred, i.uv).rgb;
  float coc = min(abs(d - FOCUS) / RANGE, MAX_COC);
  return float4(lerp(sharp.rgb, blur, coc), sharp.a);
}
