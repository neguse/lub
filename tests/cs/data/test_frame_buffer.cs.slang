StructuredBuffer<float4> src;
RWStructuredBuffer<float4> dst;

[shader("compute")][numthreads(1, 1, 1)] void cs_main() { dst[0] = src[0]; }
