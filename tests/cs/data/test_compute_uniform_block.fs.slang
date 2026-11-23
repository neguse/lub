// Visualizes the compute output: R = marker (mark[1].x), G/B = uniform value
// (val[0]).
StructuredBuffer<float4> mark;
StructuredBuffer<float4> val;

[shader("fragment")] float4 fs_main() : SV_Target {
  return float4(mark[1].x, val[0].y, val[0].z, 1.0);
}
