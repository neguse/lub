// G-buffer geometry pass VS. Scene vertices are already in world space
// (model baked on the CPU), so the model matrix is identity: view then proj.
// We carry view-space position + view-space normal so screen-space effects
// (SSAO, fog, DoF) work in a single, well-conditioned space.
#ifndef LUB_12_GBUFFER_UNIFORMS
#define LUB_12_GBUFFER_UNIFORMS
struct GBufUniforms {
  float4x4 proj;
  float4x4 view;
  float4x4 light_mvp;    // world -> light clip, for the shadow lookup
  float4 light_dir_view; // light direction in view space (xyz), w unused
};
ConstantBuffer<GBufUniforms> u;
#endif

struct VSIn {
  float3 pos : POSITION;
  float3 normal : NORMAL;
  float4 color : COLOR;
};

struct VSOut {
  float4 pos : SV_Position;
  float3 vpos : TEXCOORD0; // view-space position
  float3 vn : TEXCOORD1;   // view-space normal
  float3 ldir : TEXCOORD2; // light dir (view space), forwarded so the FS
                           // needs no uniform block of its own (only one
                           // uniform block per shader is wired up).
  float4 lpos : TEXCOORD3; // light-clip position for shadow mapping
  float4 color : COLOR;
};

[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  float4 vp = mul(u.view, float4(i.pos, 1.0));
  o.vpos = vp.xyz;
  o.pos = mul(u.proj, vp);
  o.vn = mul((float3x3)u.view, i.normal);
  o.ldir = u.light_dir_view.xyz;
  o.lpos = mul(u.light_mvp, float4(i.pos, 1.0));
  o.color = i.color;
  return o;
}
