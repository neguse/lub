struct FSIn { float4 col : COLOR; };
[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target { return i.col; }
