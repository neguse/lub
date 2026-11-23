// The destination written by the storage texture case. Its contents are not
// checked.
RWTexture2D<float4> dst;

[shader("compute")][numthreads(1, 1, 1)] void
cs_main(uint3 tid : SV_DispatchThreadID) {
  dst[tid.xy] = float4(0.0, 0.0, 1.0, 1.0);
}
