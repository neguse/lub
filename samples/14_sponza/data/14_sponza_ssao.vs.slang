struct U { float4 params; }; // x=proj00, y=proj11
ConstantBuffer<U> u;

struct VSIn { float2 pos : POSITION; float2 uv : TEXCOORD0; };
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float2 proj : TEXCOORD1; };

[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    o.pos = float4(i.pos, 0.0, 1.0);
    o.uv = i.uv;
    o.proj = u.params.xy;
    return o;
}
