// Final grade: 3D LUT lookup, animated film grain, and gamma correction.
// The LUT is a 16^3 cube flattened into a 256x16 texture:
// x = r within one blue slice, y = green row, slices laid out along x.
LUB_TEXTURE2D(scene);
LUB_TEXTURE2D(lut);

struct U { float4 params; }; // x=time, y=grain, z=lut mix, w=gamma
ConstantBuffer<U> u;

static const float LUT_N = 16.0;

struct FSIn { float2 uv : TEXCOORD0; };

float hash12(float2 p) {
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float3 sample_lut(float3 c) {
    c = saturate(c);
    float b = c.b * (LUT_N - 1.0);
    float b0 = floor(b);
    float b1 = min(b0 + 1.0, LUT_N - 1.0);
    float bt = b - b0;
    float2 uv0 = float2((b0 * LUT_N + c.r * (LUT_N - 1.0) + 0.5) / (LUT_N * LUT_N),
                        (c.g * (LUT_N - 1.0) + 0.5) / LUT_N);
    float2 uv1 = float2((b1 * LUT_N + c.r * (LUT_N - 1.0) + 0.5) / (LUT_N * LUT_N),
                        uv0.y);
    float3 a = LUB_SAMPLE_LOD(lut, uv0).rgb;
    float3 d = LUB_SAMPLE_LOD(lut, uv1).rgb;
    return lerp(a, d, bt);
}

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    float3 c = LUB_SAMPLE_LOD(scene, i.uv).rgb;

    float3 graded = sample_lut(c);
    c = lerp(c, graded, saturate(u.params.z));

    float grain = hash12(i.uv * float2(1920.0, 1080.0) + u.params.x * 37.0) - 0.5;
    c += grain * u.params.y;

    c = pow(saturate(c), float3(1.0 / max(u.params.w, 0.001)));
    return float4(c, 1.0);
}
