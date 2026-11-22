// dst[i] = src[i] * 2 + 1. Compute used to check, from the pixels of rt_b,
// whether a dispatch queued after a readback ran.
StructuredBuffer<float> src;
RWStructuredBuffer<float> dst;

[shader("compute")][numthreads(4, 1, 1)] void
cs_main(uint3 tid : SV_DispatchThreadID) {
  dst[tid.x] = src[tid.x] * 2.0 + 1.0;
}
