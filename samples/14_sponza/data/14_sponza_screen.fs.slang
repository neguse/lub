LUB_TEXTURE2D(scene);

struct U { float4 params; }; // x=mode, y=amount
ConstantBuffer<U> u;

struct FSIn { float2 uv : TEXCOORD0; };

float unpack_depth(float3 v) {
    return dot(v, float3(1.0, 1.0 / 255.0, 1.0 / 65025.0));
}

float3 srgb_to_linear(float3 c) {
    return pow(max(c, 0.0), float3(2.2));
}

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    int mode = int(u.params.x + 0.5);
    float4 center = LUB_SAMPLE_LOD(scene, i.uv);
    float3 c = center.rgb;
    float alpha = 0.0;

    if (mode == 0 || mode == 8) {
        c = center.rgb;              // linear beauty
        alpha = 1.0;                 // final present should tonemap
    } else if (mode == 1) {
        c = srgb_to_linear(center.rgb); // albedo
    } else if (mode == 2) {
        c = center.rgb;              // encoded normal
    } else if (mode == 3) {
        float d = saturate(center.z / 34.0);
        c = float3(d, d, d);
    } else if (mode == 4 || mode == 5 || mode == 6) {
        float v = saturate(center.a);
        if (mode == 6) v = saturate(center.r);
        c = float3(v, v, v);
    } else if (mode == 7) {
        float d = unpack_depth(center.rgb);
        c = float3(d, d, d);
    }

    return float4(c, alpha);
}
