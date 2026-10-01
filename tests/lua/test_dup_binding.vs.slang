// VS と FS が同じ名前の StructuredBuffer と texture を読む。
// 期待色の内訳: VS が item[0] (red) + tex (green の半分)、
// FS が item[1] (blue) + tex (green の半分) を足す。
StructuredBuffer<float4> item;
LUB_TEXTURE2D(tex);

static const float2 quad[6] = {float2(-1, -1), float2(1, -1), float2(1, 1),
                               float2(-1, -1), float2(1, 1),  float2(-1, 1)};

struct VSOut {
  float4 pos : SV_Position;
  float2 uv : TEXCOORD0;
  float3 vcol : COLOR0;
};

[shader("vertex")] VSOut vs_main(uint vid
                                 : LUB_VERTEX_ID) {
  float2 p = quad[vid];
  VSOut o;
  o.pos = float4(p, 0.0, 1.0);
  o.uv = p * 0.5 + 0.5;
  o.vcol = item[0].rgb + LUB_SAMPLE_LOD(tex, float2(0.5, 0.5)).rgb * 0.5;
  return o;
}
