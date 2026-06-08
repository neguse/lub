LUB_TEXTURE2D(scene);
LUB_TEXTURE2D(gpos);

struct U {
  float4 r0;
  float4 r1;
  float4 r2;
  float4 r3;
};
ConstantBuffer<U> u;

static const int SAMPLES = 9;
static const float STRENGTH = 0.45;
static const float MAX_UV = 0.04;

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 base = LUB_SAMPLE_LOD(scene, i.uv);
  float4 P = LUB_SAMPLE_LOD(gpos, i.uv);
  if (P.z <= 0.0001)
    return base;

  float4 vp = float4(P.xyz, 1.0);
  float4 prevClip =
      float4(dot(u.r0, vp), dot(u.r1, vp), dot(u.r2, vp), dot(u.r3, vp));
  if (abs(prevClip.w) < 1.0e-5)
    return base;
  float2 prevUV = prevClip.xy / prevClip.w * 0.5 + 0.5;
  prevUV.y = 1.0 - prevUV.y;

  float2 vel = (i.uv - prevUV) * STRENGTH;
  float mag = length(vel);
  if (mag > MAX_UV)
    vel *= MAX_UV / mag;
  if (mag < 1.0e-5)
    return base;

  float3 acc = float3(0.0);
  for (int k = 0; k < SAMPLES; ++k) {
    float t = (float(k) / float(SAMPLES - 1)) - 0.5;
    acc += LUB_SAMPLE_LOD(scene, i.uv + vel * t).rgb;
  }
  return float4(acc / float(SAMPLES), base.a);
}
