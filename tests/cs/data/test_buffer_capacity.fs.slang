StructuredBuffer<float4> col;

struct Uniforms {
  float4 declared; // x = number of declared elements
};
ConstantBuffer<Uniforms> u;

struct FSIn {
  float4 pos : SV_Position;
};

// Number of elements visible from the shader.
// Metal shaders cannot query the count.
// clang-format off
uint element_count() {
  __target_switch {
  case metal:
    return uint(u.declared.x);
  default:
    uint n, stride;
    col.GetDimensions(n, stride);
    return n;
  }
}
// clang-format on

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return col[element_count() - 1];
}
