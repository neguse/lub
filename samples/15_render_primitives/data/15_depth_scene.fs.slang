struct FSIn {
  float3 color : COLOR;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float d = i.color.r;
  return float4(d, d, d, 1.0);
}
