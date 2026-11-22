// Reads acc[0].x, adds 1 and writes it back, and writes 1 to column u.texel.x
// of img. Both build on the contents written by the previous dispatch.
struct Params {
  float4 texel;
};
ConstantBuffer<Params> u;

RWStructuredBuffer<float4> acc;
RWTexture2D<float4> img;

[shader("compute")][numthreads(1, 1, 1)] void
cs_main(uint3 tid : SV_DispatchThreadID) {
  acc[0] = acc[0] + float4(1.0, 0.0, 0.0, 0.0);
  img[int2((int)u.texel.x, 0)] = float4(1.0, 1.0, 1.0, 1.0);
}
