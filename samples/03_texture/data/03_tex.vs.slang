struct V {
  float3 pos;
  float pad0;
  float2 uv;
  float2 pad1;
};
StructuredBuffer<V> verts;
struct VSOut {
  float2 uv : TEXCOORD0;
  float4 pos : SV_Position;
};
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V i = verts[vid];
  VSOut o;
  o.pos = float4(i.pos, 1.0);
  o.uv = i.uv;
  return o;
}
