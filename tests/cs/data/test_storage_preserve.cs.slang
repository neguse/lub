// acc[0].x を読んで 1 足して書き、img の u.texel.x 列に 1 を書く。
// どちらも前の dispatch が書いた内容に重ねる。
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
