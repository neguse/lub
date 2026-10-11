LUB_TEXTURE2D(tex);

struct FsU {
  float4 tint; // x = B に書く値
};
ConstantBuffer<FsU> f;

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 c = LUB_SAMPLE(tex, float2(0.5, 0.5));
  return float4(c.r, c.g, f.tint.x, 1.0);
}
