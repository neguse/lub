// Compute shader: writes 3 vertex positions (vec4: xy = pos, zw = color.rg)
// into a storage buffer. The graphics pass then rebinds the same buffer as a
// vertex buffer and draws a single triangle.

struct Vertex {
  float2 pos;
  float2 col;
};
RWStructuredBuffer<Vertex> out_verts;

[shader("compute")][numthreads(3, 1, 1)] void
cs_main(uint3 tid : SV_DispatchThreadID) {
  Vertex v;
  if (tid.x == 0) {
    v.pos = float2(-0.6, -0.5);
    v.col = float2(1.0, 0.2);
  } else if (tid.x == 1) {
    v.pos = float2(0.6, -0.5);
    v.col = float2(0.2, 1.0);
  } else {
    v.pos = float2(0.0, 0.6);
    v.col = float2(0.2, 0.2);
  }
  out_verts[tid.x] = v;
}
