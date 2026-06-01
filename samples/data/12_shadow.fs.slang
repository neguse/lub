// Shadow depth pass FS: pack the light-space depth into RGBA8 (depth textures
// aren't sampleable here, so we store depth in colour like 11_shadow).
struct FSIn { float depth : TEXCOORD0; };

float3 pack_depth(float v) {
    float3 enc = frac(v * float3(1.0, 255.0, 65025.0));
    enc -= enc.yzz * float3(1.0 / 255.0, 1.0 / 255.0, 0.0);
    return enc;
}

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    return float4(pack_depth(saturate(i.depth)), 1.0);
}
