// Fullscreen-quad VS for SSAO.
struct V {
  float2 pos;
  float2 uv;
};
StructuredBuffer<V> verts;
struct VSOut {
  float2 uv : TEXCOORD0;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V i = verts[vid];
  VSOut o;
  o.pos = float4(i.pos, 0.0, 1.0);
  o.uv = i.uv;
  return o;
}
