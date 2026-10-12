StructuredBuffer<float4> item;
LUB_TEXTURE2D(tex);

struct FSIn {
  float4 pos : SV_Position;
  float2 uv : TEXCOORD0;
  float3 vcol : COLOR0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float3 c = i.vcol + item[1].rgb + LUB_SAMPLE_LOD(tex, i.uv).rgb * 0.5;
  return float4(c, 1.0);
}
