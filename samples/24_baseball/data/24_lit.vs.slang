struct Uniforms {
  float4x4 mvp;
  float4x4 model;
  float4 tint;
};
ConstantBuffer<Uniforms> u;
struct VSIn {
  float3 pos : POSITION;
  float3 normal : NORMAL;
  float4 color : COLOR;
};
struct VSOut {
  float3 normal : TEXCOORD1; // world
  float4 color : COLOR0;
  float4 pos : SV_Position;
};
[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = mul(u.mvp, float4(i.pos, 1.0));
  // model は回転+平行移動 (+均一スケール) のみなので normal は w=0 変換で足りる
  o.normal = mul(u.model, float4(i.normal, 0.0)).xyz;
  o.color = i.color * u.tint;
  return o;
}
