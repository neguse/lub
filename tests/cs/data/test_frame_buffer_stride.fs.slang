// 要素の大きさが 16 の倍数でない範囲。
StructuredBuffer<float3> col;

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return float4(col[0], 1.0);
}
