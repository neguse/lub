LUB_TEXTURE2D(scene);

static const float THRESHOLD = 0.90;
static const float INTENSITY = 0.85;

struct FSIn { float2 uv : TEXCOORD0; };

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    float3 c = LUB_SAMPLE_LOD(scene, i.uv).rgb;
    float lum = dot(c, float3(0.2126, 0.7152, 0.0722));
    float k = max(lum - THRESHOLD, 0.0) / max(lum, 1.0e-4);
    return float4(c * k * INTENSITY, 1.0);
}
