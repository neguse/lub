RWStructuredBuffer<float4> col;

[shader("compute")][numthreads(1, 1, 1)] void cs_main() {
  col[0] = float4(0, 1, 0, 1);
}
