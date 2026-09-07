// Material G-buffer VS for the textured hero object. Same view-space setup as
// 12_gbuffer.vs but carries UV; light dir is forwarded as a varying so the FS
// needs no uniform block of its own.
#ifndef LUB_12_MAT_UNIFORMS
#define LUB_12_MAT_UNIFORMS
struct MatUniforms {
  float4x4 proj;
  float4x4 view;
  float4x4 model;
  float4x4 light_mvp;
  float4 light_dir_view;
};
ConstantBuffer<MatUniforms> u;
#endif

// Io.InterleavePnu layout (12 floats per vertex); the procedural hero
// sphere is generated with the same pads.
struct V {
  float3 pos;
  float pad0;
  float3 normal;
  float pad1;
  float2 uv;
  float2 pad2;
};
StructuredBuffer<V> verts;

struct VSOut {
  float3 vpos : TEXCOORD0;
  float3 vn : TEXCOORD1;
  float3 ldir : TEXCOORD2;
  float2 uv : TEXCOORD3;
  float4 lpos : TEXCOORD4;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V i = verts[vid];
  VSOut o;
  float4 wp = mul(u.model, float4(i.pos, 1.0));
  float4 vp = mul(u.view, wp);
  o.vpos = vp.xyz;
  o.pos = mul(u.proj, vp);
  // model is translation + uniform scale, so its 3x3 only scales the normal;
  // the view rotation then takes it to view space. normalize in the FS.
  o.vn = mul((float3x3)u.view, mul((float3x3)u.model, i.normal));
  o.ldir = u.light_dir_view.xyz;
  o.uv = i.uv;
  o.lpos = mul(u.light_mvp, wp);
  return o;
}
