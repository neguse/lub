LUB_TEXTURE2D(scene);

struct FSParams {
  float4 tint;
  float4 mode;
};
ConstantBuffer<FSParams> fu;

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 c = LUB_SAMPLE_LOD(scene, i.uv);
  if (fu.mode.x > 0.5) {
    float d = c.r;
    c = float4(d, d, d, 1.0);
  }
  return float4(saturate(c.rgb * fu.tint.rgb), 1.0);
}
