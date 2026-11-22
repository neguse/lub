struct VSOut {
  float4 pos : SV_Position;
};

// A triangle that covers the whole target.
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  VSOut o;
  o.pos = float4(vid == 1 ? 3.0 : -1.0, vid == 2 ? 3.0 : -1.0, 0.0, 1.0);
  return o;
}
