// R = tex、G = uniform block の値。
LUB_TEXTURE2D(tex);

struct U {
  float4 v;
};
ConstantBuffer<U> u;

struct FSIn {
  float4 pos : SV_Position;
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return float4(LUB_SAMPLE_LOD(tex, i.uv).r, u.v.x, 0.0, 1.0);
}
