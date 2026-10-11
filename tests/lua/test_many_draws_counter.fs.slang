// compute の結果を 1 画素に書く: R, G = 数 (下位, 上位 byte)、B, A = 最後の
// dispatch の番号 (下位, 上位 byte)。
StructuredBuffer<float4> counter;

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 c = counter[0];
  return float4(fmod(c.x, 256.0), floor(c.x / 256.0), fmod(c.y, 256.0),
                floor(c.y / 256.0)) /
         255.0;
}
