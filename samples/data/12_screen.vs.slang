// Fullscreen-quad VS that forwards the screen-effect mode + amount to the
// fragment stage (uniform kept in the VS; see 12_ssao.vs for why).
struct U { float4 params; }; // x=mode, y=amount
ConstantBuffer<U> u;

struct VSIn  { float2 pos : POSITION; float2 uv : TEXCOORD0; };
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float2 info : TEXCOORD1; };

[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    o.pos = float4(i.pos, 0.0, 1.0);
    o.uv = i.uv;
    o.info = u.params.xy;
    return o;
}
