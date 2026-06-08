LUB_TEXTURE2D(diffuse);
struct FSIn {
  float2 uv : TEXCOORD0;
};
[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return LUB_SAMPLE(diffuse, i.uv);
}
