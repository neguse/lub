struct VSOut {
  float4 pos : SV_Position;
};

// target 全体を覆う三角形。
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  VSOut o;
  o.pos = float4(vid == 1 ? 3.0 : -1.0, vid == 2 ? 3.0 : -1.0, 0.0, 1.0);
  return o;
}
