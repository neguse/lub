// dst[i] = src[i] * 2 + 1。読み戻しの後に積んだ dispatch が実行されたかを
// rt_b の画素で確かめるための compute。
StructuredBuffer<float> src;
RWStructuredBuffer<float> dst;

[shader("compute")][numthreads(4, 1, 1)] void
cs_main(uint3 tid : SV_DispatchThreadID) {
  dst[tid.x] = src[tid.x] * 2.0 + 1.0;
}
