struct LightUniforms {
    float4 light0; // xyz view-space light direction, w intensity
    float4 light1;
    float4 params; // x exposure, y ambient
};
ConstantBuffer<LightUniforms> u;

struct VSIn {
    float2 pos : POSITION;
    float2 uv : TEXCOORD0;
};

struct VSOut {
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
    float4 light0 : TEXCOORD1;
    float4 light1 : TEXCOORD2;
    float4 params : TEXCOORD3;
};

[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    o.pos = float4(i.pos, 0.0, 1.0);
    o.uv = i.uv;
    o.light0 = u.light0;
    o.light1 = u.light1;
    o.params = u.params;
    return o;
}
