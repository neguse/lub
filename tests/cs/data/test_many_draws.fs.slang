// tex and tex2 are 2x1 textures; R of texel 0 is 0, R of texel 1 is 255,
// and G of both is the texture number. PROBE samples between the texels (the
// value changes with filter) and outside the right edge (the value changes with
// wrap) and mixes them, so the 4 combinations of (filter, wrap) give different
// values.
LUB_TEXTURE2D(tex);
LUB_TEXTURE2D(tex2);

#define PROBE(t)                                                               \
  (0.25 * LUB_SAMPLE(t, float2(0.625, 0.5)).r +                                \
   0.75 * LUB_SAMPLE(t, float2(1.125, 0.5)).r)

struct FsU {
  float4 tint; // x = the value written to B
};
ConstantBuffer<FsU> f;

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float id = LUB_SAMPLE(tex, float2(0.5, 0.5)).g;
  return float4(PROBE(tex), PROBE(tex2), f.tint.x, id);
}
