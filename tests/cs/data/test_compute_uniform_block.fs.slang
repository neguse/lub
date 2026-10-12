// compute の出力を可視化する: R = 目印 (mark[1].x)、G/B = uniform の値
// (val[0])。
StructuredBuffer<float4> mark;
StructuredBuffer<float4> val;

[shader("fragment")] float4 fs_main() : SV_Target {
  return float4(mark[1].x, val[0].y, val[0].z, 1.0);
}
