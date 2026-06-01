// Separable 9-tap Gaussian blur, vertical. Texel size baked for a 1280x720
// target (see 12_fog.fs for why there is no uniform block).
LUB_TEXTURE2D(scene);

static const float2 STEP = float2(0.0, 1.0 / 720.0);

struct FSIn { float2 uv : TEXCOORD0; };

float3 blur9(float2 uv, float2 o) {
    float w0 = 0.227027, w1 = 0.1945946, w2 = 0.1216216, w3 = 0.054054, w4 = 0.016216;
    float3 c = LUB_SAMPLE_LOD(scene, uv).rgb * w0;
    c += LUB_SAMPLE_LOD(scene, uv + o * 1.0).rgb * w1;
    c += LUB_SAMPLE_LOD(scene, uv - o * 1.0).rgb * w1;
    c += LUB_SAMPLE_LOD(scene, uv + o * 2.0).rgb * w2;
    c += LUB_SAMPLE_LOD(scene, uv - o * 2.0).rgb * w2;
    c += LUB_SAMPLE_LOD(scene, uv + o * 3.0).rgb * w3;
    c += LUB_SAMPLE_LOD(scene, uv - o * 3.0).rgb * w3;
    c += LUB_SAMPLE_LOD(scene, uv + o * 4.0).rgb * w4;
    c += LUB_SAMPLE_LOD(scene, uv - o * 4.0).rgb * w4;
    return c;
}

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    return float4(blur9(i.uv, STEP), 1.0);
}
