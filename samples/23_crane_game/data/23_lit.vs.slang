struct Uniforms {
  float4x4 mvp;
  float4x4 model;
  float4 color;
};
ConstantBuffer<Uniforms> u;

struct VSIn {
  float3 pos : POSITION;
  float3 normal : NORMAL;
};
struct VSOut {
  float3 normal : NORMAL;
  float4 color : COLOR;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = mul(u.mvp, float4(i.pos, 1.0));
  // Rigid body transforms only (rotation + translation), so transforming the
  // normal by the model matrix with w=0 is exact.
  o.normal = mul(u.model, float4(i.normal, 0.0)).xyz;
  o.color = u.color;
  return o;
}
