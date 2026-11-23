// The usual HLSL style: textures and samplers are declared separately. A
// sampler whose name starts with a texture name pairs with that texture
// regardless of the declaration order (right_sampler is declared before its
// texture and before left_sampler). lut is read only with Load and has no
// sampler. u = 1.25 lands on texel 0 with the REPEAT of left, and with the
// CLAMP of right on texel 1, so it shows whether each texture sampled with its
// own sampler.
SamplerState right_sampler;
Texture2D left;
Texture2D right;
SamplerState left_sampler;
Texture2D lut;

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 l = left.SampleLevel(left_sampler, float2(1.25, 0.5), 0.0);
  float4 r = right.SampleLevel(right_sampler, float2(1.25, 0.5), 0.0);
  return (i.uv.x < 0.5 ? l : r) * lut.Load(int3(0, 0, 0));
}
