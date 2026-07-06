struct VSIn {
  float2 pos : POSITION;
  float4 color : COLOR;
};
struct VSOut {
  float4 color : COLOR;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = float4(i.pos, 0.0, 1.0);
  o.color = i.color;
  return o;
}
