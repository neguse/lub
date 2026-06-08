struct FillParams {
  float4 fill;
};
ConstantBuffer<FillParams> u;

[shader("fragment")] float4 fs_main() : SV_Target { return u.fill; }
