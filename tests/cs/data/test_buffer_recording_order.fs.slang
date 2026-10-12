StructuredBuffer<float4> col;

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target { return col[0]; }
