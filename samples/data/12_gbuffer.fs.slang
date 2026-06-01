// G-buffer geometry pass FS. Writes three render targets:
//   SV_Target0 gColor    : Blinn-Phong lit color (rgb), a = emissive mask
//   SV_Target1 gNormal   : view-space normal, encoded *0.5+0.5
//   SV_Target2 gPosition : view-space position (xyz), w = linear depth (vpos.z)
// View space here is left-handed (camera looks down +Z), so vpos.z grows with
// distance and the background (cleared to z=0) reads as "no geometry".
// The directional light is shadow-mapped (PCF against a packed-depth map).
LUB_TEXTURE2D(shadow_map);

struct FSIn {
    float3 vpos : TEXCOORD0;
    float3 vn : TEXCOORD1;
    float3 ldir : TEXCOORD2;
    float4 lpos : TEXCOORD3;
    float4 color : COLOR;
};

struct FSOut {
    float4 gColor : SV_Target0;
    float4 gNormal : SV_Target1;
    float4 gPosition : SV_Target2;
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

[shader("fragment")]
FSOut fs_main(FSIn i) {
    float3 N = normalize(i.vn);
    float3 L = normalize(i.ldir);
    float3 V = normalize(-i.vpos);           // camera at origin in view space
    float3 H = normalize(L + V);

    float shadow = 0.35 + 0.65 * shadow_factor(i.lpos); // keep some ambient in shadow
    float ndl = saturate(dot(N, L)) * shadow;
    float ndh = saturate(dot(N, H));
    float spec = pow(ndh, 48.0) * 0.45 * shadow;
    float ambient = 0.22;

    float3 lit = i.color.rgb * (ambient + ndl * 0.9) + spec;

    FSOut o;
    o.gColor = float4(lit, i.color.a);
    o.gNormal = float4(N * 0.5 + 0.5, 1.0);
    o.gPosition = float4(i.vpos, i.vpos.z);
    return o;
}
