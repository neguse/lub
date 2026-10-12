// dispatch i は src[0].x に 1 を足して dst[0] に書く (i == 0 は 0 から数える)。
// dispatch が飛ぶか、別の dispatch の table を読むと数がずれる。
struct Params {
  float4 step; // x = この dispatch の番号
};
ConstantBuffer<Params> u;

StructuredBuffer<float4> src;
RWStructuredBuffer<float4> dst;

[shader("compute")][numthreads(1, 1, 1)] void
cs_main(uint3 tid : SV_DispatchThreadID) {
  float prev = u.step.x == 0.0 ? 0.0 : src[0].x;
  dst[0] = float4(prev + 1.0, u.step.x, 0.0, 0.0);
}
