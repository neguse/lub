StructuredBuffer<float2> verts;
static const uint idx[6] = {0, 1, 2, 0, 2, 3};

struct VSOut {
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  VSOut o;
  o.pos = float4(verts[idx[vid]], 0.0, 1.0);
  return o;
}
