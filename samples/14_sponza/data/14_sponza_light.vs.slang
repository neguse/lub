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
    float4 vl0 : TEXCOORD4;
    float4 vl1 : TEXCOORD5;
    float4 vl2 : TEXCOORD6;
    float4 vl3 : TEXCOORD7;
};

[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    o.pos = float4(i.pos, 0.0, 1.0);
    o.uv = i.uv;
    o.light0 = u.light0;
    o.light1 = u.light1;
    o.params = u.params;
    o.vl0 = u.vl0;
    o.vl1 = u.vl1;
    o.vl2 = u.vl2;
    o.vl3 = u.vl3;
    return o;
}
