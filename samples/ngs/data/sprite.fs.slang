LUB_TEXTURE2D(atlas);
struct FSIn {
  float2 uv : TEXCOORD0;
  float4 color : COLOR;
};
[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 c = LUB_SAMPLE(atlas, i.uv) * i.color;
  if (c.a < 0.004)
    discard;
  return c;
}
