// Shares one sampler between 2 textures. lub binds a sampler per texture, so
// this cannot be expressed, and the shader compile fails with an error.
Texture2D first;
Texture2D second;
SamplerState shared;

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return first.SampleLevel(shared, i.uv, 0.0) +
         second.SampleLevel(shared, i.uv, 0.0);
}
