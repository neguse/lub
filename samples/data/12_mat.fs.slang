// Material G-buffer FS: textured + normal-mapped + cel + rim + specular hero.
// Writes the same MRT layout as 12_gbuffer.fs so the whole post chain applies.
// The tangent basis is derived per-pixel from screen-space derivatives
// (Mikkelsen cotangent frame), so no per-vertex tangent attribute is needed.
LUB_TEXTURE2D(albedo);
LUB_TEXTURE2D(normalmap);
LUB_TEXTURE2D(shadow_map);

struct FSIn {
    float3 vpos : TEXCOORD0;
    float3 vn : TEXCOORD1;
    float3 ldir : TEXCOORD2;
    float2 uv : TEXCOORD3;
    float4 lpos : TEXCOORD4;
};

float unpack_depth(float3 v) {
    return dot(v, float3(1.0, 1.0 / 255.0, 1.0 / 65025.0));
}

float shadow_factor(float4 lpos) {
    float3 ndc = lpos.xyz / lpos.w;
    float2 uv = ndc.xy * 0.5 + 0.5;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 ||
        ndc.z < 0.0 || ndc.z > 1.0) return 1.0;
    float texel = 1.0 / 1024.0;
    float bias = 0.004;
    float lit = 0.0;
    for (int y = -1; y <= 1; ++y)
        for (int x = -1; x <= 1; ++x) {
            float closest = unpack_depth(
                LUB_SAMPLE(shadow_map, uv + float2(float(x), float(y)) * texel).rgb);
            lit += (ndc.z - bias <= closest) ? 1.0 : 0.0;
        }
    return lit / 9.0;
}

struct FSOut {
    float4 gColor : SV_Target0;
    float4 gNormal : SV_Target1;
    float4 gPosition : SV_Target2;
};

float3 perturb_normal(float3 N, float3 vpos, float2 uv) {
    float3 dp1 = ddx(vpos);
    float3 dp2 = ddy(vpos);
    float2 duv1 = ddx(uv);
    float2 duv2 = ddy(uv);
    float3 dp2perp = cross(dp2, N);
    float3 dp1perp = cross(N, dp1);
    float3 T = dp2perp * duv1.x + dp1perp * duv2.x;
    float3 B = dp2perp * duv1.y + dp1perp * duv2.y;
    float invmax = rsqrt(max(dot(T, T), dot(B, B)));
    float3 nm = LUB_SAMPLE(normalmap, uv).xyz * 2.0 - 1.0;
    return normalize(T * (nm.x * invmax) + B * (nm.y * invmax) + N * nm.z);
}

[shader("fragment")]
FSOut fs_main(FSIn i) {
    float3 alb = LUB_SAMPLE(albedo, i.uv).rgb;
    float3 N = perturb_normal(normalize(i.vn), i.vpos, i.uv);
    float3 L = normalize(i.ldir);
    float3 V = normalize(-i.vpos);
    float3 H = normalize(L + V);

    float shadow = 0.35 + 0.65 * shadow_factor(i.lpos);
    float ndl = saturate(dot(N, L)) * shadow;
    float cel = floor(ndl * 3.0 + 0.5) / 3.0;             // toon diffuse bands
    float spec = step(0.6, pow(saturate(dot(N, H)), 48.0)) * shadow; // toon spec
    float rim = pow(1.0 - saturate(dot(N, V)), 3.0);        // fresnel rim

    float ambient = 0.22;
    float3 lit = alb * (ambient + cel * 0.85)
               + spec * 0.6
               + rim * float3(0.35, 0.5, 0.85);

    FSOut o;
    o.gColor = float4(lit, 1.0);
    o.gNormal = float4(N * 0.5 + 0.5, 1.0);
    o.gPosition = float4(i.vpos, i.vpos.z);
    return o;
}
