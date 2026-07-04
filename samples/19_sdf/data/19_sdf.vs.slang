struct Uniforms {
  float4x4 mvp;
  float4x4 model;
  float4x4 view;
  float4 params; // x = metal override (0..1)
};
ConstantBuffer<Uniforms> u;
struct VSIn {
  float3 pos : POSITION;
  float3 normal : NORMAL;
  float3 color : COLOR;
  float2 mr : TEXCOORD0; // metallic, roughness (頂点に焼いた材質)
};
struct VSOut {
  float4 pos : SV_Position;
  float3 normal : TEXCOORD1;  // world
  float3 vnormal : TEXCOORD2; // view space (matcap 用)
  float3 color : COLOR0;
  float2 mr : TEXCOORD3;
};
[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = mul(u.mvp, float4(i.pos, 1.0));
  float3 wn = mul((float3x3)u.model, i.normal);
  o.normal = wn;
  o.vnormal = mul((float3x3)u.view, wn);
  o.color = i.color;
  o.mr = i.mr;
  return o;
}
