StructuredBuffer<float2> verts;
static const uint idx[6] = {0, 1, 2, 0, 2, 3};

struct VSOut {
  float4 pos : SV_Position;
  float2 uv : TEXCOORD0;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  float2 p = verts[idx[vid]];
  VSOut o;
  o.pos = float4(p, 0.0, 1.0);
  o.uv = p * 0.5 + 0.5;
  return o;
}
