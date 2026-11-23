// Reads integer STORAGE through StructuredBuffer<int>; 1 if it matches the
// passed values (7, 123456, 2^24 + 1, -5). R is use_vals (UseBufferInts), G is
// frame_vals (FrameBufferInts).
StructuredBuffer<int> use_vals;
StructuredBuffer<int> frame_vals;

bool matches(StructuredBuffer<int> v) {
  return v[0] == 7 && v[1] == 123456 && v[2] == 16777217 && v[3] == -5;
}

[shader("fragment")] float4 fs_main() : SV_Target {
  return float4(matches(use_vals) ? 1.0 : 0.0, matches(frame_vals) ? 1.0 : 0.0,
                0.0, 1.0);
}
