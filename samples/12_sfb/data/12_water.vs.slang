// Fullscreen-quad VS for the water pass. Forwards three rows of inverse(view)
// (enough to recover a view-space point's world x/y/z) plus params (time,
// waterY) to the FS as varyings.
struct U {
  float4 ir0;    // inverse(view) row 0 -> world.x
  float4 ir1;    // row 1 -> world.y
  float4 ir2;    // row 2 -> world.z
  float4 params; // x=time, y=waterY
};
ConstantBuffer<U> u;

struct VSIn {
  float2 pos : POSITION;
  float2 uv : TEXCOORD0;
};
struct VSOut {
  float2 uv : TEXCOORD0;
  float4 a : TEXCOORD1;
  float4 b : TEXCOORD2;
  float4 c : TEXCOORD3;
  float4 p : TEXCOORD4;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = float4(i.pos, 0.0, 1.0);
  o.uv = i.uv;
  o.a = u.ir0;
  o.b = u.ir1;
  o.c = u.ir2;
  o.p = u.params;
  return o;
}
