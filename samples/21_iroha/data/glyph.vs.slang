struct Uniforms {
  float4
      psr; // x, y (screen px), scale (px per em), rotation (rad, CCW in y-up)
  float4 tint;
  float4 screen; // logical w, h
  float4 center; // rotation/placement center in em (glyph bbox center)
};
ConstantBuffer<Uniforms> u;

struct VSIn {
  float2 pos : POSITION; // em units, y-up, baseline origin
};

struct VSOut {
  float4 pos : SV_Position;
  float4 color : COLOR;
};

[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  float c = cos(u.psr.w);
  float s = sin(u.psr.w);
  float2 l = (i.pos - u.center.xy) * u.psr.z;
  float2 r = float2(l.x * c - l.y * s, l.x * s + l.y * c);
  float2 p = float2(u.psr.x + r.x, u.psr.y - r.y); // y-up -> screen y-down
  o.pos = float4(p.x / u.screen.x * 2.0 - 1.0, 1.0 - p.y / u.screen.y * 2.0,
                 0.0, 1.0);
  o.color = u.tint;
  return o;
}
