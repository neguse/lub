// storage texture の case が書く先。中身は検査しない。
RWTexture2D<float4> dst;

[shader("compute")][numthreads(1, 1, 1)] void
cs_main(uint3 tid : SV_DispatchThreadID) {
  dst[tid.xy] = float4(0.0, 0.0, 1.0, 1.0);
}
