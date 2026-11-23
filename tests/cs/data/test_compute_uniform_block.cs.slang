// Compute with a uniform block. tid 0 writes the uniform value to out_buf[0],
// and tid 1 writes a constant marker to out_buf[1]. The dispatch without
// uniforms (2 groups) leaves the marker, and the dispatch with uniforms (1
// group) the value, in separate buffers (keeping the contents of the previous
// dispatch is checked by test_storage_preserve).
struct Params {
  float4 value;
};
ConstantBuffer<Params> u;

RWStructuredBuffer<float4> out_buf;

[shader("compute")][numthreads(1, 1, 1)] void
cs_main(uint3 tid : SV_DispatchThreadID) {
  if (tid.x == 0) {
    out_buf[0] = u.value;
  } else if (tid.x == 1) {
    out_buf[1] = float4(0.5, 0.0, 0.0, 1.0);
  }
}
