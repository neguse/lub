struct Uniforms {
  float4 side; // x = 0 で target の左半分、1 で右半分を覆う
};
ConstantBuffer<Uniforms> u;

static const float2 quad[6] = {float2(0, 0), float2(1, 0), float2(1, 1),
                               float2(0, 0), float2(1, 1), float2(0, 1)};

struct VSOut {
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid
                                 : LUB_VERTEX_ID) {
  float2 p = quad[vid];
  VSOut o;
  o.pos = float4(u.side.x - 1.0 + p.x, p.y * 2.0 - 1.0, 0.0, 1.0);
  return o;
}
