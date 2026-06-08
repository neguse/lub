LUB_TEXTURE2D(g_albedo);
LUB_TEXTURE2D(g_normal);
LUB_TEXTURE2D(g_position);
LUB_TEXTURE2D(shadow_map);
LUB_TEXTURE2D(ao_map);

struct LightUniforms {
    float4 light0; // xyz view-space light direction, w intensity
    float4 light1;
    float4 params; // x light scale, y ambient, z shadow strength, w ao strength
    float4 vl0;    // view-space position -> light clip rows
    float4 vl1;
    float4 vl2;
    float4 vl3;
};
ConstantBuffer<LightUniforms> u;

static const float PI = 3.14159265359;

struct FSIn {
    float2 uv : TEXCOORD0;
};

float3 srgb_to_linear(float3 c) {
    return pow(max(c, 0.0), float3(2.2));
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

float unpack_depth(float3 v) {
    return dot(v, float3(1.0, 1.0 / 255.0, 1.0 / 65025.0));
}

float shadow_factor(float4 lpos) {
    if (abs(lpos.w) < 1.0e-5) return 1.0;
    float3 ndc = lpos.xyz / lpos.w;
    float2 uv = ndc.xy * 0.5 + 0.5;
    uv.y = 1.0 - uv.y;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 ||
        ndc.z < 0.0 || ndc.z > 1.0) return 1.0;

    float texel = 1.0 / 2048.0;
    float bias = 0.0016;
    float lit = 0.0;
    for (int y = -1; y <= 1; ++y)
        for (int x = -1; x <= 1; ++x) {
            float closest = unpack_depth(
                LUB_SAMPLE_LOD(shadow_map, uv + float2(float(x), float(y)) * texel).rgb);
            lit += (ndc.z - bias <= closest) ? 1.0 : 0.0;
        }
    return lit / 9.0;
}

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    float4 ar = LUB_SAMPLE_LOD(g_albedo, i.uv);
    float4 nm = LUB_SAMPLE_LOD(g_normal, i.uv);
    float4 gp = LUB_SAMPLE_LOD(g_position, i.uv);
    if (gp.z <= 0.0001) {
        float t = saturate(1.0 - i.uv.y);
        float3 sky = lerp(float3(0.010, 0.012, 0.016), float3(0.045, 0.055, 0.070), t);
        return float4(sky, 1.0);
    }

    float3 albedo = srgb_to_linear(ar.rgb);
    float roughness = clamp(ar.a, 0.045, 1.0);
    float3 N = normalize(nm.xyz * 2.0 - 1.0);
    float metallic = saturate(nm.a);
    float3 V = normalize(-gp.xyz);
    float4 vp = float4(gp.xyz, 1.0);
    float4 lpos = float4(dot(u.vl0, vp), dot(u.vl1, vp), dot(u.vl2, vp), dot(u.vl3, vp));
    float shadow = lerp(1.0, shadow_factor(lpos), u.params.z);
    float ao = lerp(1.0, saturate(LUB_SAMPLE_LOD(ao_map, i.uv).r), u.params.w);

    float3 color = albedo * u.params.y * ao;
    color += pbr_light(albedo, metallic, roughness, N, V, normalize(u.light0.xyz), u.light0.w) * shadow;
    color += pbr_light(albedo, metallic, roughness, N, V, normalize(u.light1.xyz), u.light1.w);

    color *= u.params.x;
    return float4(max(color, 0.0), 1.0);
}
