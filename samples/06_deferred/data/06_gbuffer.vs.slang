struct V {
  float3 pos;
  float pad0;
  float4 col0;
  float4 col1;
};
StructuredBuffer<V> verts;
struct VSOut {
  float4 col0 : COLOR0;
  float4 col1 : COLOR1;
  float4 pos : SV_Position;
};
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V i = verts[vid];
  VSOut o;
  o.pos = float4(i.pos, 1.0);
  o.col0 = i.col0;
  o.col1 = i.col1;
  return o;
}
