// Binds only dst; tex, src, and scratch are not passed.
// The values of resources that are not passed differ per backend, so they do
// not affect the value written to dst.
Texture2D tex;
StructuredBuffer<float4> src;
RWTexture2D<float4> scratch;
RWStructuredBuffer<float4> dst;

[shader("compute")][numthreads(1, 1, 1)] void cs_main() {
  float4 v = float4(0, 1, 0, 1);
  float4 seen = tex.Load(int3(0, 0, 0)) + src[0];
  scratch[uint2(0, 0)] = seen;
  dst[0] = seen.x == 12345.0 ? seen : v;
}
