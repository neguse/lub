// u.col.x 列だけに描く。
// tex の R が u.expect.x から u.expect.y 以内なら R = 1、外れていれば R = 0。
struct Params {
  float4 col;
  float4 expect;
};
ConstantBuffer<Params> u;

LUB_TEXTURE2D(tex);

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  if (abs(i.pos.x - 0.5 - u.col.x) > 0.25)
    discard;
  float r = LUB_SAMPLE(tex, float2(0.5, 0.5)).r;
  return float4(abs(r - u.expect.x) <= u.expect.y ? 1.0 : 0.0, 0.0, 0.0, 1.0);
}
