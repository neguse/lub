struct Uniforms {
  float4 cell; // xy = この draw が塗る画素、zw = target の幅と高さ
};
ConstantBuffer<Uniforms> u;

static const float2 quad[6] = {float2(0, 0), float2(1, 0), float2(1, 1),
                               float2(0, 0), float2(1, 1), float2(0, 1)};

struct VSOut {
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid
                                 : LUB_VERTEX_ID) {
  float2 p = (u.cell.xy + quad[vid]) / u.cell.zw;
  VSOut o;
  o.pos = float4(p.x * 2.0 - 1.0, 1.0 - p.y * 2.0, 0.0, 1.0);
  return o;
}
