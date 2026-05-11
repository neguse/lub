struct FSIn { float4 col0 : COLOR0; float4 col1 : COLOR1; };
struct FSOut { float4 target0 : SV_Target0; float4 target1 : SV_Target1; };
[shader("fragment")]
FSOut fs_main(FSIn i) {
    FSOut o;
    o.target0 = i.col0;
    o.target1 = i.col1;
    return o;
}
