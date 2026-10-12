// target 全体を覆う三角形。
struct VSOut {
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  float2 p = float2((vid << 1) & 2, vid & 2);
  VSOut o;
  o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
  return o;
}
