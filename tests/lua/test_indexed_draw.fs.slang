struct FSIn {
  float4 pos : SV_Position;
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return float4(i.uv.x, i.uv.y, 0.4, 1.0);
}
