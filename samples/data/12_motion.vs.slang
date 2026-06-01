// Fullscreen-quad VS for camera motion blur. Forwards the 4 rows of the
// reprojection matrix (prevViewProj * inverse(currentView)) to the FS as
// varyings. Uniform kept in the vertex stage (see 12_ssao.vs for why).
struct U {
    float4 r0;
    float4 r1;
    float4 r2;
    float4 r3;
};
ConstantBuffer<U> u;

struct VSIn  { float2 pos : POSITION; float2 uv : TEXCOORD0; };
struct VSOut {
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
    float4 m0 : TEXCOORD1;
    float4 m1 : TEXCOORD2;
    float4 m2 : TEXCOORD3;
    float4 m3 : TEXCOORD4;
};

[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    o.pos = float4(i.pos, 0.0, 1.0);
    o.uv = i.uv;
    o.m0 = u.r0;
    o.m1 = u.r1;
    o.m2 = u.r2;
    o.m3 = u.r3;
    return o;
}
