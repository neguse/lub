struct Uniforms {
  float4 side; // x = 0 covers the left half of the target, 1 the right half
};
ConstantBuffer<Uniforms> u;

static const float2 quad[6] = {float2(0, 0), float2(1, 0), float2(1, 1),
                               float2(0, 0), float2(1, 1), float2(0, 1)};

struct VSOut {
  float4 pos : SV_Position;
};

// vid 6..11 (the index declared later in the index case) is the right-half
// quad.
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  float2 p = quad[vid % 6];
  float x = u.side.x + float(vid / 6);
  VSOut o;
  o.pos = float4(x - 1.0 + p.x, p.y * 2.0 - 1.0, 0.0, 1.0);
  return o;
}
