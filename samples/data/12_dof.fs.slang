// Depth of field: lerp between the sharp beauty buffer and a pre-blurred copy
// by a circle-of-confusion derived from |view depth - FOCUS|. Background
// (gPosition.z == 0) stays sharp. Constants baked (see 12_fog.fs for why there
// is no uniform block).
LUB_TEXTURE2D(scene);   // sharp beauty
LUB_TEXTURE2D(blurred); // gaussian-blurred beauty
LUB_TEXTURE2D(gpos);

static const float FOCUS = 3.2;     // view-space distance in focus
static const float RANGE = 3.5;     // distance over which it fully defocuses
static const float MAX_COC = 0.85;  // cap so the focal plane keeps some clarity

struct FSIn { float2 uv : TEXCOORD0; };

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    float3 sharp = LUB_SAMPLE(scene, i.uv).rgb;
    float d = LUB_SAMPLE(gpos, i.uv).z;
    if (d <= 0.0001) return float4(sharp, 1.0);
    float3 blur = LUB_SAMPLE(blurred, i.uv).rgb;
    float coc = min(abs(d - FOCUS) / RANGE, MAX_COC);
    return float4(lerp(sharp, blur, coc), 1.0);
}
