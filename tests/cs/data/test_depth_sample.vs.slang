struct Uniforms {
  float4 z; // x = clip z
};
ConstantBuffer<Uniforms> u;

struct V {
  float2 pos;
};
StructuredBuffer<V> verts;

struct VSOut {
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V i = verts[vid];
  VSOut o;
  o.pos = float4(i.pos, u.z.x, 1.0);
  return o;
}
