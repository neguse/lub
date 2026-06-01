// Linear distance fog. Blends toward FOG_COLOR by view-space linear depth
// (gPosition.z). Background (z == 0) is left untouched. No uniform block: the
// backend's fragment-stage uniform binding clashes with sampled textures, so
// tuning values are baked here (still hot-reloadable — edit and save).
LUB_TEXTURE2D(scene);
LUB_TEXTURE2D(gpos);

static const float FOG_START = 4.0;
static const float FOG_END = 15.0;
static const float3 FOG_COLOR = float3(0.09, 0.12, 0.15);

struct FSIn { float2 uv : TEXCOORD0; };

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    float3 c = LUB_SAMPLE(scene, i.uv).rgb;
    float d = LUB_SAMPLE(gpos, i.uv).z;
    if (d <= 0.0001) return float4(c, 1.0);
    float f = saturate((d - FOG_START) / (FOG_END - FOG_START));
    return float4(lerp(c, FOG_COLOR, f), 1.0);
}
