struct VSIn  { float2 pos : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float4 color : COLOR; };
[shader("vertex")]
VSOut vs_main(VSIn i) {
    VSOut o;
    // logical 640x480, origin top-left -> NDC (origin center, y up)
    o.pos = float4(i.pos.x / 320.0 - 1.0, 1.0 - i.pos.y / 240.0, 0.0, 1.0);
    o.uv = i.uv;
    o.color = i.color;
    return o;
}
