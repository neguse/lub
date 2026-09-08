struct V {
  float2 pos;
  float z;
  float pad0;
};
StructuredBuffer<V> verts;
struct VSOut {
  float3 color : COLOR;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V i = verts[vid];
  VSOut o;
  o.pos = float4(i.pos, i.z, 1.0);
  o.color = float3(i.z, 1.0 - i.z, 0.35);
  return o;
}
