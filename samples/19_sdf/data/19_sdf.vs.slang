struct Uniforms {
  float4x4 mvp;
  float4x4 model;
};
ConstantBuffer<Uniforms> u;
struct VSIn {
  float3 pos : POSITION;
  float3 normal : NORMAL;
};
struct VSOut {
  float4 pos : SV_Position;
  float3 normal : TEXCOORD0;
};
[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = mul(u.mvp, float4(i.pos, 1.0));
  o.normal = mul((float3x3)u.model, i.normal);
  return o;
}
