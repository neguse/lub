LUB_TEXTURE2D(scene);
LUB_TEXTURE2D(gnormal);
LUB_TEXTURE2D(gpos);

static const float DEPTH_WEIGHT = 2.3;
static const float NORMAL_WEIGHT = 0.18;
static const float3 INK = float3(0.004, 0.004, 0.006);

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float2 t = float2(ddx(i.uv).x, ddy(i.uv).y);
  float4 src = LUB_SAMPLE_LOD(scene, i.uv);
  float dC = LUB_SAMPLE_LOD(gpos, i.uv).z;
  if (dC <= 0.0001)
    return src;
  float3 nC = normalize(LUB_SAMPLE_LOD(gnormal, i.uv).xyz * 2.0 - 1.0);

  float2 offs[4] = {
      float2(t.x, 0.0),
      float2(-t.x, 0.0),
      float2(0.0, t.y),
      float2(0.0, -t.y),
  };

  float depthEdge = 0.0;
  float normalEdge = 0.0;
  for (int k = 0; k < 4; ++k) {
    float d = LUB_SAMPLE_LOD(gpos, i.uv + offs[k]).z;
    float3 n =
        normalize(LUB_SAMPLE_LOD(gnormal, i.uv + offs[k]).xyz * 2.0 - 1.0);
    if (d > 0.0001)
      depthEdge += abs(d - dC);
    normalEdge += 1.0 - saturate(dot(n, nC));
  }

  float e = saturate(depthEdge * DEPTH_WEIGHT + normalEdge * NORMAL_WEIGHT);
  return float4(lerp(src.rgb, INK, e), src.a);
}
