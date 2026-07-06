// Fullscreen-quad vertex shader shared by every post-process pass.
// Input is a clip-space quad (pos.xy in [-1,1]) plus uv (0,0 = top-left texel
// to match the offscreen passes that write clip y = -1 onto row 0).
struct VSIn {
  float2 pos : POSITION;
  float2 uv : TEXCOORD0;
};
struct VSOut {
  float2 uv : TEXCOORD0;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = float4(i.pos, 0.0, 1.0);
  o.uv = i.uv;
  return o;
}
