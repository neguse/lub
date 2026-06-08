// Shadow depth pass VS for the flat (world-space) scene geometry. Declares the
// full stride-10 vertex (pos/normal/color) so the buffer layout matches, but
// only uses the position.
struct U {
  float4x4 light_mvp;
};
ConstantBuffer<U> u;

struct VSIn {
  float3 pos : POSITION;
  float3 normal : NORMAL;
  float4 color : COLOR;
};
struct VSOut {
  float4 pos : SV_Position;
  float depth : TEXCOORD0;
};

[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  float4 p = mul(u.light_mvp, float4(i.pos, 1.0));
  o.pos = p;
  o.depth = p.z / p.w;
  return o;
}
