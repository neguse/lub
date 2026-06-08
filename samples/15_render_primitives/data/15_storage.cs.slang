struct Params {
  float4 params;
};
ConstantBuffer<Params> u;

RWTexture2D<float4> dst;

[shader("compute")][numthreads(8, 8, 1)] void
cs_main(uint3 tid : SV_DispatchThreadID) {
  uint w = (uint)u.params.x;
  uint h = (uint)u.params.y;
  if (tid.x >= w || tid.y >= h)
    return;

  float2 uv = (float2(tid.xy) + 0.5) /
              float2(max(u.params.x, 1.0), max(u.params.y, 1.0));
  float wave = 0.5 + 0.5 * sin((uv.x + uv.y + u.params.z) * 18.0);
  dst[tid.xy] = float4(uv.x, uv.y, wave, 1.0);
}
