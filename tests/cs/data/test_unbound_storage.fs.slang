// Binds only col; scratch is not passed (a draw has no way to pass a storage
// texture).
StructuredBuffer<float4> col;
RWTexture2D<float4> scratch;

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  scratch[uint2(0, 0)] = col[0];
  return col[0];
}
