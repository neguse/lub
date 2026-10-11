// 組になる texture の無い sampler (extra)。shader compile が error になる。
Texture2D tex;
SamplerState tex_sampler;
SamplerState extra;

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return tex.SampleLevel(tex_sampler, i.uv, 0.0) +
         tex.SampleLevel(extra, i.uv, 0.0);
}
