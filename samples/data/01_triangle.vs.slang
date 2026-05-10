struct VSIn  { float3 pos : POSITION; };
struct VSOut { float4 pos : SV_Position; };
[shader("vertex")]
VSOut vs_main(VSIn i) { VSOut o; o.pos = float4(i.pos, 1.0); return o; }
