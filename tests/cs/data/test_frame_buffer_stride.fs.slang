// 要素の大きさが 16 の倍数でない範囲。
struct Rgb {
  float r, g, b;
};
StructuredBuffer<Rgb> col;

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return float4(col[0].r, col[0].g, col[0].b, 1.0);
}
