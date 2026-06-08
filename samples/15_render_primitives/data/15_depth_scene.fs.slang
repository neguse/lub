struct FSIn {
  float3 color : COLOR;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return float4(i.color, 1.0);
}
