struct Uniforms {
  float4 z; // x = clip z
};
ConstantBuffer<Uniforms> u;

struct VSIn {
  float2 pos : POSITION;
};
struct VSOut {
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = float4(i.pos, u.z.x, 1.0);
  return o;
}
