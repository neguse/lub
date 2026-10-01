// uniform block を持つ compute。tid 0 は uniform の値を out_buf[0] に書き、
// tid 1 は定数の目印を out_buf[1] に書く。uniform 無しの dispatch
// (2 group) は目印を、uniform 有りの dispatch (1 group) は値を別々の
// buffer に残す (同じ buffer を使い回すと sdlgpu の cycle で前の内容が
// 捨てられる)。
struct Params {
  float4 value;
};
ConstantBuffer<Params> u;

RWStructuredBuffer<float4> out_buf;

[shader("compute")][numthreads(1, 1, 1)] void cs_main(uint3 tid
                                                      : SV_DispatchThreadID) {
  if (tid.x == 0) {
    out_buf[0] = u.value;
  } else if (tid.x == 1) {
    out_buf[1] = float4(0.5, 0.0, 0.0, 1.0);
  }
}
