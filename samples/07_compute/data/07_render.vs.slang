struct V {
  float2 pos;
  float2 col;
};
StructuredBuffer<V> verts;
struct VSOut {
  float4 col : COLOR;
  float4 pos : SV_Position;
};
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V i = verts[vid];
  VSOut o;
  o.pos = float4(i.pos, 0.0, 1.0);
  // Reconstruct full RGBA from the two color components compute wrote.
  o.col = float4(i.col, 1.0 - i.col.x - i.col.y, 1.0);
  return o;
}
