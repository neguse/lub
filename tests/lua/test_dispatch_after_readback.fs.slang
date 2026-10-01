// 画素の列 x に対応する dst[x] - 1 を灰色で出す。dispatch が走らなければ
// dst は 0 のままで、出力は負値の clamp で黒になる。
StructuredBuffer<float> dst;

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float v = dst[int(i.pos.x)] - 1.0;
  return float4(v, v, v, 1.0);
}
