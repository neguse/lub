// 各 channel を 1 つの resource だけで決める: R = tex_a、G = tex_b、
// B = item (VS)。
LUB_TEXTURE2D(tex_a);
LUB_TEXTURE2D(tex_b);

struct FSIn {
  float4 pos : SV_Position;
  float2 uv : TEXCOORD0;
  float4 vcol : COLOR0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float r = LUB_SAMPLE_LOD(tex_a, i.uv).r;
  float g = LUB_SAMPLE_LOD(tex_b, i.uv).g;
  return float4(r, g, i.vcol.b, 1.0);
}
