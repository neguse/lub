StructuredBuffer<float4> col;

struct Uniforms {
  float4 declared; // x = 宣言した要素の数
};
ConstantBuffer<Uniforms> u;

struct FSIn {
  float4 pos : SV_Position;
};

// shader から見える要素の数。
// Metal の shader は数を問い合わせられない。
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
