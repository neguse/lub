struct ShadowUniforms {
    float4x4 light_mvp;
    float4x4 model;
    float4 base_color_factor;
    float4 material; // x metallic, y roughness, z alpha cutoff, w alpha mode
};
ConstantBuffer<ShadowUniforms> u;

struct VSIn {
    float3 pos : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    float4 tangent : TANGENT;
};

struct VSOut {
    float4 pos : SV_Position;
    float depth : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float4 base_color_factor : TEXCOORD2;
    float4 material : TEXCOORD3;
};

[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    float4 p = mul(u.light_mvp, mul(u.model, float4(i.pos, 1.0)));
    o.pos = p;
    o.depth = p.z / p.w;
    o.uv = i.uv;
    o.base_color_factor = u.base_color_factor;
    o.material = u.material;
    return o;
}
