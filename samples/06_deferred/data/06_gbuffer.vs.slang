struct VSIn {
  float3 pos : POSITION;
  float4 col0 : COLOR0;
  float4 col1 : COLOR1;
};
struct VSOut {
  float4 pos : SV_Position;
  float4 col0 : COLOR0;
  float4 col1 : COLOR1;
};
[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = float4(i.pos, 1.0);
  o.col0 = i.col0;
  o.col1 = i.col1;
  return o;
}
