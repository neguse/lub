struct Uniforms {
  float4x4 mvp;
};
ConstantBuffer<Uniforms> u;
struct V {
  float3 pos;
  float pad0;
  float3 normal;
  float pad1;
};
StructuredBuffer<V> verts;
struct VSOut {
  float3 normal : TEXCOORD0;
  float4 pos : SV_Position;
};
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V i = verts[vid];
  VSOut o;
  o.pos = mul(u.mvp, float4(i.pos, 1.0));
  o.normal = i.normal;
  return o;
}
