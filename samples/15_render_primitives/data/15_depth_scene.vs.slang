struct VSIn {
  float2 pos : POSITION;
  float z : TEXCOORD0;
};
struct VSOut {
  float3 color : COLOR;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = float4(i.pos, i.z, 1.0);
  o.color = float3(i.z, 1.0 - i.z, 0.35);
  return o;
}
