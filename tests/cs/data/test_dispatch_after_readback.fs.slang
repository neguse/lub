// Outputs dst[x] - 1 for pixel column x as gray. If the dispatch does not run,
// dst stays 0 and the output is black because the negative value is clamped.
StructuredBuffer<float> dst;

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float v = dst[int(i.pos.x)] - 1.0;
  return float4(v, v, v, 1.0);
}
