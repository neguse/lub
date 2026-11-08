// 画面全体の三角形を 1 枚描く。
struct VSOut {
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  VSOut o;
  float2 p = float2((vid << 1) & 2, vid & 2);
  o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
  return o;
}
