// tex と tex2 は 2x1 の texture で、texel 0 の R が 0、texel 1 の R が 255、
// G はどちらも texture の番号。PROBE は texel の間 (filter で値が変わる) と
// 右端の外 (wrap で値が変わる) を引いて混ぜ、(filter, wrap) の 4 通りで
// 違う値になる。
LUB_TEXTURE2D(tex);
LUB_TEXTURE2D(tex2);

#define PROBE(t)                                                               \
  (0.25 * LUB_SAMPLE(t, float2(0.625, 0.5)).r +                                \
   0.75 * LUB_SAMPLE(t, float2(1.125, 0.5)).r)

struct FsU {
  float4 tint; // x = B に書く値
};
ConstantBuffer<FsU> f;

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float id = LUB_SAMPLE(tex, float2(0.5, 0.5)).g;
  return float4(PROBE(tex), PROBE(tex2), f.tint.x, id);
}
