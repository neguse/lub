// A triangle that covers the whole screen. uv.x is 0 at the left edge of the
// target and 1 at the right edge.
struct VSOut {
  float2 uv : TEXCOORD0;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  float2 p = float2((vid << 1) & 2, vid & 2);
  VSOut o;
  o.uv = p;
  o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
  return o;
}
