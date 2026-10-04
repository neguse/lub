struct V {
  float2 pos;
};
StructuredBuffer<V> verts;
struct VSOut {
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  VSOut o;
  o.pos = float4(verts[vid].pos, 0.5, 1.0);
  return o;
}
