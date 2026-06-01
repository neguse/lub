// Edge outline from G-buffer depth + normal discontinuities. Texel + weights
// baked for a 1280x720 target (see 12_fog.fs for why there is no uniform block).
LUB_TEXTURE2D(scene);
LUB_TEXTURE2D(gnormal);
LUB_TEXTURE2D(gpos);

static const float2 TEXEL = float2(1.0 / 1280.0, 1.0 / 720.0);
static const float DEPTH_WEIGHT = 6.0;
static const float NORMAL_WEIGHT = 0.45;
static const float3 INK = float3(0.02, 0.02, 0.04);

struct FSIn { float2 uv : TEXCOORD0; };

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    float3 c = LUB_SAMPLE(scene, i.uv).rgb;
    float dC = LUB_SAMPLE(gpos, i.uv).z;
    if (dC <= 0.0001) return float4(c, 1.0);
    float3 nC = normalize(LUB_SAMPLE(gnormal, i.uv).xyz * 2.0 - 1.0);

    float2 offs[4] = {
        float2(TEXEL.x, 0.0), float2(-TEXEL.x, 0.0),
        float2(0.0, TEXEL.y), float2(0.0, -TEXEL.y),
    };

    float depthEdge = 0.0;
    float normalEdge = 0.0;
    for (int k = 0; k < 4; ++k) {
        float d = LUB_SAMPLE(gpos, i.uv + offs[k]).z;
        float3 n = normalize(LUB_SAMPLE(gnormal, i.uv + offs[k]).xyz * 2.0 - 1.0);
        if (d > 0.0001) depthEdge += abs(d - dC);
        normalEdge += 1.0 - saturate(dot(n, nC));
    }

    float e = saturate(depthEdge * DEPTH_WEIGHT + normalEdge * NORMAL_WEIGHT);
    return float4(lerp(c, INK, e), 1.0);
}
