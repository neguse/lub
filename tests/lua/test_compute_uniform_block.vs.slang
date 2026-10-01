static const float2 quad[6] = {float2(-1, -1), float2(1, -1), float2(1, 1),
                               float2(-1, -1), float2(1, 1),  float2(-1, 1)};

struct VSOut {
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid
                                 : LUB_VERTEX_ID) {
  VSOut o;
  o.pos = float4(quad[vid], 0.0, 1.0);
  return o;
}
