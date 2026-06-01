// Fullscreen-quad VS that forwards the two projection scalars SSAO needs
// (proj[0][0], proj[1][1]) to the fragment stage as a varying. The uniform
// block lives in the vertex stage on purpose: a fragment-stage uniform block
// alongside sampled textures gets an inconsistent descriptor binding in the
// backend, so we keep uniforms in the VS (which has no textures).
struct U { float4 params; }; // x=proj00, y=proj11 (y already includes the y-flip)
ConstantBuffer<U> u;

struct VSIn  { float2 pos : POSITION; float2 uv : TEXCOORD0; };
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float2 proj : TEXCOORD1; };

[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    o.pos = float4(i.pos, 0.0, 1.0);
    o.uv = i.uv;
    o.proj = u.params.xy;
    return o;
}
