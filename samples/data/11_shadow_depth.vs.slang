struct Uniforms { float4x4 light_mvp; };
ConstantBuffer<Uniforms> u;

struct VSIn {
    float3 pos : POSITION;
    float3 normal : NORMAL;
    float4 color : COLOR;
};

struct VSOut {
    float4 pos : SV_Position;
    float depth : TEXCOORD0;
};

[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    float4 p = mul(u.light_mvp, float4(i.pos, 1.0));
    o.pos = p;
    o.depth = p.z / p.w;
    return o;
}
