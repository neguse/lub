// Screen-space ambient occlusion in view space, folded into the lit color.
// Projection is reduced to the two scalars (proj00, proj11), so a view-space
// sample point projects to uv as:
//   suv.x = (proj00 * x / z) * 0.5 + 0.5
//   suv.y = (proj11 * y / z) * 0.5 + 0.5
// The G-buffer is an offscreen target stored y-down, so the sample uv still
// needs suv.y = 1 - suv.y (same flip the shadow map needs); without it the AO
// samples a vertically mirrored location and smears a floating band across the
// floor behind objects.
LUB_TEXTURE2D(scene);
LUB_TEXTURE2D(gnormal);
LUB_TEXTURE2D(gpos);

struct U { float4 params; }; // x=proj00, y=proj11
ConstantBuffer<U> u;

static const float RADIUS = 0.3;
static const float BIAS = 0.03;
static const float STRENGTH = 0.7;
static const int N_SAMPLES = 24;

struct FSIn { float2 uv : TEXCOORD0; };

float hash12(float2 p) {
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    float3 base = LUB_SAMPLE_LOD(scene, i.uv).rgb;
    float3 P = LUB_SAMPLE_LOD(gpos, i.uv).xyz;
    if (P.z <= 0.0001) return float4(base, 1.0);

    float3 N = normalize(LUB_SAMPLE_LOD(gnormal, i.uv).xyz * 2.0 - 1.0);
    float3 up = abs(N.z) < 0.99 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
    float3 T = normalize(cross(up, N));
    float3 B = cross(N, T);

    float rnd = hash12(i.uv * float2(1920.0, 1080.0)) * 6.2831853;
    float occ = 0.0;
    for (int s = 0; s < N_SAMPLES; ++s) {
        float fi = (float(s) + 0.5) / float(N_SAMPLES);
        float ang = rnd + float(s) * 2.39996323;
        float r = RADIUS * sqrt(fi);
        float2 disc = float2(cos(ang), sin(ang)) * r;
        float3 sp = P + (T * disc.x + B * disc.y) + N * (r * 0.6);

        float2 suv = float2(u.params.x * sp.x / sp.z, u.params.y * sp.y / sp.z) * 0.5 + 0.5;
        suv.y = 1.0 - suv.y; // G-buffer is offscreen (y-down) vs the sample uv
        if (suv.x < 0.0 || suv.x > 1.0 || suv.y < 0.0 || suv.y > 1.0) continue;

        float sampleDepth = LUB_SAMPLE_LOD(gpos, suv).z;
        if (sampleDepth <= 0.0001) continue;

        float rangeCheck = smoothstep(0.0, 1.0, RADIUS / max(abs(P.z - sampleDepth), 1e-4));
        occ += (sampleDepth <= sp.z - BIAS ? 1.0 : 0.0) * rangeCheck;
    }

    float ao = saturate(1.0 - (occ / float(N_SAMPLES)) * STRENGTH);
    return float4(base * ao, 1.0);
}
