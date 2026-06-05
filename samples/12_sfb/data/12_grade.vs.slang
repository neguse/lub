// Fullscreen-quad VS for final film grain + LUT + gamma correction. Uniforms
// stay in the VS and are forwarded as varyings so the FS only owns textures.
struct U { float4 params; }; // x=time, y=grain, z=lut mix, w=gamma
ConstantBuffer<U> u;

struct VSIn  { float2 pos : POSITION; float2 uv : TEXCOORD0; };
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float4 params : TEXCOORD1; };

[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    o.pos = float4(i.pos, 0.0, 1.0);
    o.uv = i.uv;
    o.params = u.params;
    return o;
}
