struct FSIn {
  float4 pos : SV_Position;
  float4 col : COLOR0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target { return i.col; }
