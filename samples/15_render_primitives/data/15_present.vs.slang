struct VSParams {
  float4 transform;
};
ConstantBuffer<VSParams> vu;

struct VSIn {
  float2 pos : POSITION;
  float2 uv : TEXCOORD0;
};
struct VSOut {
  float4 pos : SV_Position;
  float2 uv : TEXCOORD0;
};

[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  float2 p = i.pos * vu.transform.xy + vu.transform.zw;
  o.pos = float4(p, 0.0, 1.0);
  o.uv = i.uv;
  return o;
}
