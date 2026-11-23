// dispatch i adds 1 to src[0].x and writes it to dst[0] (i == 0 counts from 0).
// If a dispatch is skipped or reads the table of another dispatch, the count is
// off.
struct Params {
  float4 step; // x = the number of this dispatch
};
ConstantBuffer<Params> u;

StructuredBuffer<float4> src;
RWStructuredBuffer<float4> dst;

[shader("compute")][numthreads(1, 1, 1)] void
cs_main(uint3 tid : SV_DispatchThreadID) {
  float prev = u.step.x == 0.0 ? 0.0 : src[0].x;
  dst[0] = float4(prev + 1.0, u.step.x, 0.0, 0.0);
}
