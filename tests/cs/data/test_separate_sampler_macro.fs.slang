// test_separate_sampler.fs.slang と同じ絵を LUB_TEXTURE2D で書いたもの。
LUB_TEXTURE2D(left);
LUB_TEXTURE2D(right);

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 l = LUB_SAMPLE_LOD(left, float2(1.25, 0.5));
  float4 r = LUB_SAMPLE_LOD(right, float2(1.25, 0.5));
  return i.uv.x < 0.5 ? l : r;
}
