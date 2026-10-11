struct Uniforms {
  float4 side; // x = 0 で target の左半分、1 で右半分を覆う
};
ConstantBuffer<Uniforms> u;

static const float2 quad[6] = {float2(0, 0), float2(1, 0), float2(1, 1),
                               float2(0, 0), float2(1, 1), float2(0, 1)};

struct VSOut {
  float4 pos : SV_Position;
};

// vid 6..11 (index の case で後に宣言した index) は右半分の quad。
[shader("vertex")] VSOut vs_main(uint vid
                                 : LUB_VERTEX_ID) {
  float2 p = quad[vid % 6];
  float x = u.side.x + float(vid / 6);
  VSOut o;
  o.pos = float4(x - 1.0 + p.x, p.y * 2.0 - 1.0, 0.0, 1.0);
  return o;
}
