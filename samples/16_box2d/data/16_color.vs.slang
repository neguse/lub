struct V {
  float3 pos;
  float pad0;
  float4 color;
};
StructuredBuffer<V> verts;

struct VSOut {
  float4 color : COLOR;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V i = verts[vid];
  VSOut o;
  o.pos = float4(i.pos, 1.0);
  o.color = i.color;
  return o;
}
