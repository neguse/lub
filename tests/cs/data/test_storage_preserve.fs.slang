// Pixel x: R = column x of img, G = acc[0].x (the count itself, 0..255).
LUB_TEXTURE2D(img);
StructuredBuffer<float4> acc;

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float2 uv = float2(i.pos.x / 6.0, 0.5);
  return float4(LUB_SAMPLE(img, uv).r, acc[0].x / 255.0, 0.0, 1.0);
}
