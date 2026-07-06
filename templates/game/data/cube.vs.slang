struct Uniforms {
  float4x4 mvp;
};
ConstantBuffer<Uniforms> u;
struct VSIn {
  float3 pos : POSITION;
  float4 color : COLOR;
};
struct VSOut {
  float4 color : COLOR;
  float4 pos : SV_Position;
};
[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = mul(u.mvp, float4(i.pos, 1.0));
  o.color = i.color;
  return o;
}
