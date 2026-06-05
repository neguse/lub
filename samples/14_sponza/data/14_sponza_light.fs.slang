LUB_TEXTURE2D(g_albedo);
LUB_TEXTURE2D(g_normal);
LUB_TEXTURE2D(g_position);

static const float PI = 3.14159265359;

struct FSIn {
    float2 uv : TEXCOORD0;
    float4 light0 : TEXCOORD1;
    float4 light1 : TEXCOORD2;
    float4 params : TEXCOORD3;
};

float3 srgb_to_linear(float3 c) {
    return pow(max(c, 0.0), float3(2.2));
}

float3 linear_to_srgb(float3 c) {
    return pow(max(c, 0.0), float3(1.0 / 2.2));
}

float distribution_ggx(float ndh, float roughness) {
    float a = roughness * roughness;
    float a2 = a * a;
    float d = ndh * ndh * (a2 - 1.0) + 1.0;
    return a2 / max(PI * d * d, 1.0e-5);
}

float geometry_schlick_ggx(float ndv, float roughness) {
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return ndv / max(ndv * (1.0 - k) + k, 1.0e-5);
}

float3 fresnel_schlick(float cosTheta, float3 f0) {
    return f0 + (1.0 - f0) * pow(1.0 - cosTheta, 5.0);
}

float3 pbr_light(float3 albedo, float metallic, float roughness, float3 N, float3 V, float3 L, float intensity) {
    float3 H = normalize(V + L);
    float ndl = saturate(dot(N, L));
    float ndv = max(saturate(dot(N, V)), 0.001);
    float ndh = saturate(dot(N, H));
    float hdn = saturate(dot(H, V));

    float3 f0 = lerp(float3(0.04), albedo, metallic);
    float3 F = fresnel_schlick(hdn, f0);
    float D = distribution_ggx(ndh, roughness);
    float G = geometry_schlick_ggx(ndv, roughness) * geometry_schlick_ggx(ndl, roughness);
    float3 specular = (D * G * F) / max(4.0 * ndv * ndl, 0.001);
    float3 kd = (1.0 - F) * (1.0 - metallic);
    return (kd * albedo / PI + specular) * intensity * ndl;
}

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    float4 ar = LUB_SAMPLE_LOD(g_albedo, i.uv);
    float4 nm = LUB_SAMPLE_LOD(g_normal, i.uv);
    float4 gp = LUB_SAMPLE_LOD(g_position, i.uv);
    if (gp.a < 0.5) {
        float v = 0.02 + 0.035 * (1.0 - i.uv.y);
        return float4(v, v * 1.08, v * 1.18, 1.0);
    }

    float3 albedo = srgb_to_linear(ar.rgb);
    float roughness = clamp(ar.a, 0.045, 1.0);
    float3 N = normalize(nm.xyz * 2.0 - 1.0);
    float metallic = saturate(nm.a);
    float3 V = normalize(-gp.xyz);

    float3 color = albedo * i.params.y;
    color += pbr_light(albedo, metallic, roughness, N, V, normalize(i.light0.xyz), i.light0.w);
    color += pbr_light(albedo, metallic, roughness, N, V, normalize(i.light1.xyz), i.light1.w);

    color *= i.params.x;
    color = color / (color + 1.0);
    return float4(linear_to_srgb(color), 1.0);
}
