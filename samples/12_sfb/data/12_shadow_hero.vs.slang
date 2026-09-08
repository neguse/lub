// Shadow depth pass VS for the hero (12-float pos/normal/uv, transformed by
// its model matrix). Matches 12_mat.vs's world transform so the cast shadow
// lines up with the lit object.
struct U {
  float4x4 light_mvp;
  float4x4 model;
};
ConstantBuffer<U> u;

// Io.InterleavePnu layout (12 floats per vertex); the procedural hero
// sphere is generated with the same pads.
struct V {
  float3 pos;
  float pad0;
  float3 normal;
  float pad1;
  float2 uv;
  float2 pad2;
};
StructuredBuffer<V> verts;
struct VSOut {
  float depth : TEXCOORD0;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V i = verts[vid];
  VSOut o;
  float4 world = mul(u.model, float4(i.pos, 1.0));
  float4 p = mul(u.light_mvp, world);
  o.pos = p;
  o.depth = p.z / p.w;
  return o;
}
