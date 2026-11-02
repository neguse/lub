// col だけを束ね、scratch は渡さない (draw には storage texture を渡す手段が無い)。
StructuredBuffer<float4> col;
RWTexture2D<float4> scratch;

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  scratch[uint2(0, 0)] = col[0];
  return col[0];
}
