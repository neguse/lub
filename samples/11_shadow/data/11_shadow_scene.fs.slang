LUB_TEXTURE2D(shadow_map);

struct FSIn {
    float3 normal : TEXCOORD0;
    float4 color : COLOR;
    float4 light_pos : TEXCOORD1;
};

float unpack_depth(float3 v) {
    return dot(v, float3(1.0, 1.0 / 255.0, 1.0 / 65025.0));
}

float shadow_factor(float4 light_pos) {
    float3 ndc = light_pos.xyz / light_pos.w;
    float2 uv = ndc.xy * 0.5 + 0.5;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 ||
        ndc.z < 0.0 || ndc.z > 1.0) {
        return 1.0;
    }

    float texel = 1.0 / 1024.0;
    float bias = 0.0045;
    float strength = 0.48;
    float lit = 0.0;
    for (int y = -1; y <= 1; ++y) {
        for (int x = -1; x <= 1; ++x) {
            float2 duv = uv + float2((float)x, (float)y) * texel;
            // LUB_SAMPLE_LOD (explicit LOD 0) avoids implicit derivatives so the
            // sample is legal under WebGPU's "textureSample must be in uniform
            // control flow" rule (this runs inside shadow_factor's early-return +
            // PCF loop). Shadow map has no mips, so LOD 0 is identical to Sample.
            float closest = unpack_depth(LUB_SAMPLE_LOD(shadow_map, duv).rgb);
            lit += (ndc.z - bias <= closest) ? 1.0 : strength;
        }
    }
    return lit / 9.0;
}

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    float3 n = normalize(i.normal);
    float3 to_light = normalize(float3(-0.48, 1.0, -0.32));
    float diffuse = saturate(dot(n, to_light));
    float hemi = 0.22 + 0.18 * saturate(n.y);
    float shadow = shadow_factor(i.light_pos);
    float3 rgb = i.color.rgb * (hemi + diffuse * 0.88) * shadow;
    return float4(rgb, i.color.a);
}
