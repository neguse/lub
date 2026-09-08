struct Uniforms {
  float4x4 light_mvp;
};
ConstantBuffer<Uniforms> u;

struct V {
  float3 pos;
  float pad0;
  float3 normal;
  float pad1;
  float4 color;
};
StructuredBuffer<V> verts;

struct VSOut {
  float depth : TEXCOORD0;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V i = verts[vid];
  VSOut o;
  float4 p = mul(u.light_mvp, float4(i.pos, 1.0));
  o.pos = p;
  o.depth = p.z / p.w;
  return o;
}
