// Lighting/view-pass VS: applies a (scale.xy, offset.xy) transform from
// uniforms so the same fullscreen quad vertex buffer can be reused for both
// the left and right halves of the screen.
struct Uniforms {
  float4 transform;
};
ConstantBuffer<Uniforms> u;
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
  o.pos = float4(i.pos.x * u.transform.x + u.transform.z,
                 i.pos.y * u.transform.y + u.transform.w, 0.0, 1.0);
  o.uv = i.uv;
  return o;
}
