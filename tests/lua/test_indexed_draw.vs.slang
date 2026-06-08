struct VSIn {
  float2 pos : POSITION;
};
struct VSOut {
  float4 pos : SV_Position;
  float2 uv : TEXCOORD0;
};

[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = float4(i.pos, 0.0, 1.0);
  o.uv = i.pos * 0.5 + 0.5;
  return o;
}
