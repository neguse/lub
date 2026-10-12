struct Uniforms {
  float4 zcol; // x = clip z, yzw = rgb
};
ConstantBuffer<Uniforms> u;

struct V {
  float2 pos;
};
StructuredBuffer<V> verts;
struct VSOut {
  float4 color : COLOR;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V i = verts[vid];
  VSOut o;
  o.pos = float4(i.pos, u.zcol.x, 1.0);
  o.color = float4(u.zcol.yzw, 1.0);
  return o;
}
