// Draws only in column u.col.x.
// R = 1 if the R of tex is within u.expect.y of u.expect.x, R = 0 otherwise.
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
