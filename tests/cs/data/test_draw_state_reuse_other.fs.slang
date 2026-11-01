// tex の binding は test_draw_state_reuse.fs.slang と違う。
LUB_TEXTURE2D(tex2);
LUB_TEXTURE2D(tex);

struct FsU {
  float4 tint;
};
ConstantBuffer<FsU> f;

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return float4(LUB_SAMPLE(tex, float2(0.5, 0.5)).r,
                LUB_SAMPLE(tex2, float2(0.5, 0.5)).r, f.tint.x, 1.0);
}
