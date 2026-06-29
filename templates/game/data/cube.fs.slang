struct FSIn {
  float4 color : COLOR;
};
[shader("fragment")] float4 fs_main(FSIn i) : SV_Target { return i.color; }
