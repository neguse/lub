struct FSIn { float3 normal : TEXCOORD0; };
[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    float3 c = normalize(i.normal) * 0.5 + 0.5;
    return float4(c, 1.0);
}
