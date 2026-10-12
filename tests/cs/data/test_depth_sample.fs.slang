[shader("fragment")] float4 fs_main() : SV_Target {
  // depth-only pass: color attachment が無いので値は捨てられる。
  return float4(0.0, 0.0, 0.0, 1.0);
}
