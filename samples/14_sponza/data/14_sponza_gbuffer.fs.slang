LUB_TEXTURE2D(base_color);
LUB_TEXTURE2D(metallic_roughness);
LUB_TEXTURE2D(normal_map);

struct FSIn {
    float3 vpos : TEXCOORD0;
    float3 vn : TEXCOORD1;
    float2 uv : TEXCOORD2;
    float4 vt : TEXCOORD3;
    float4 lpos : TEXCOORD4;
    float4 base_color_factor : TEXCOORD5;
    float4 material : TEXCOORD6;
    float4 normal_params : TEXCOORD7;
};

struct FSOut {
    float4 gAlbedoRoughness : SV_Target0;
    float4 gNormalMetallic : SV_Target1;
    float4 gPosition : SV_Target2;
};

float3 derivative_tbn_normal(float3 N, float3 vpos, float2 uv, float3 nm) {
    float3 dp1 = ddx(vpos);
    float3 dp2 = ddy(vpos);
    float2 duv1 = ddx(uv);
    float2 duv2 = ddy(uv);
    float3 dp2perp = cross(dp2, N);
    float3 dp1perp = cross(N, dp1);
    float3 T = dp2perp * duv1.x + dp1perp * duv2.x;
    float3 B = dp2perp * duv1.y + dp1perp * duv2.y;
    float invmax = rsqrt(max(max(dot(T, T), dot(B, B)), 1.0e-8));
    return normalize(T * (nm.x * invmax) + B * (nm.y * invmax) + N * nm.z);
}

float3 perturb_normal(float3 N, float4 T4, float3 vpos, float2 uv, float scale) {
    float3 nm = LUB_SAMPLE_LOD(normal_map, uv).xyz * 2.0 - 1.0;
    nm.xy *= scale;
    float3 fallbackN = derivative_tbn_normal(N, vpos, uv, nm);
    float3 T = normalize(T4.xyz - N * dot(N, T4.xyz));
    float3 B = normalize(cross(N, T) * T4.w);
    float3 tangentN = normalize(T * nm.x + B * nm.y + N * nm.z);
    if (abs(T4.w) < 0.5) {
        return fallbackN;
    }
    return tangentN;
}

[shader("fragment")]
FSOut fs_main(FSIn i) {
    float4 base = LUB_SAMPLE_LOD(base_color, i.uv) * i.base_color_factor;
    float3 N = perturb_normal(normalize(i.vn), i.vt, i.vpos, i.uv, i.normal_params.x);
    if (i.material.w > 0.5 && base.a < i.material.z) {
        discard;
    }

    float4 mr = LUB_SAMPLE_LOD(metallic_roughness, i.uv);
    float roughness = saturate(mr.g * i.material.y);
    float metallic = saturate(mr.b * i.material.x);

    FSOut o;
    o.gAlbedoRoughness = float4(base.rgb, roughness);
    o.gNormalMetallic = float4(N * 0.5 + 0.5, metallic);
    o.gPosition = float4(i.vpos, i.vpos.z);
    return o;
}
