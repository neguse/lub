// Shadow depth pass VS for the hero (stride-8 pos/normal/uv, transformed by its
// model matrix). Matches 12_mat.vs's world transform so the cast shadow lines
// up with the lit object.
struct U {
  float4x4 light_mvp;
  float4x4 model;
};
ConstantBuffer<U> u;

struct VSIn {
  float3 pos : POSITION;
  float3 normal : NORMAL;
  float2 uv : TEXCOORD0;
};
struct VSOut {
  float depth : TEXCOORD0;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  float4 world = mul(u.model, float4(i.pos, 1.0));
  float4 p = mul(u.light_mvp, world);
  o.pos = p;
  o.depth = p.z / p.w;
  return o;
}
