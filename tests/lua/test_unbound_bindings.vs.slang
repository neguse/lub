StructuredBuffer<float4> item;

static const float2 tri[3] = {float2(-1, -1), float2(3, -1), float2(-1, 3)};

struct VSOut {
  float4 pos : SV_Position;
  float2 uv : TEXCOORD0;
  float4 vcol : COLOR0;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  float2 p = tri[vid];
  VSOut o;
  o.pos = float4(p, 0.0, 1.0);
  o.uv = p * 0.5 + 0.5;
  o.vcol = item[0];
  return o;
}
