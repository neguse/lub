// VS and FS read a StructuredBuffer and a texture with the same names.
// Breakdown of the expected color: VS adds item[0] (red) + tex (half of green),
// and FS adds item[1] (blue) + tex (half of green).
StructuredBuffer<float4> item;
LUB_TEXTURE2D(tex);

static const float2 quad[6] = {float2(-1, -1), float2(1, -1), float2(1, 1),
                               float2(-1, -1), float2(1, 1),  float2(-1, 1)};

struct VSOut {
  float4 pos : SV_Position;
  float2 uv : TEXCOORD0;
  float3 vcol : COLOR0;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  float2 p = quad[vid];
  VSOut o;
  o.pos = float4(p, 0.0, 1.0);
  o.uv = p * 0.5 + 0.5;
  o.vcol = item[0].rgb + LUB_SAMPLE_LOD(tex, float2(0.5, 0.5)).rgb * 0.5;
  return o;
}
