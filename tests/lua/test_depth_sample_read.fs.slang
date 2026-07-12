LUB_TEXTURE2D(dtex);

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float d = LUB_SAMPLE_LOD(dtex, i.uv).r;
  return float4(d, d, d, 1.0);
}
