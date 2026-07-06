#ifndef LUB_11_SHADOW_SCENE_UNIFORMS
#define LUB_11_SHADOW_SCENE_UNIFORMS
struct ShadowSceneUniforms {
  float4x4 mvp;
  float4x4 light_mvp;
};
ConstantBuffer<ShadowSceneUniforms> u;
#endif

struct VSIn {
  float3 pos : POSITION;
  float3 normal : NORMAL;
  float4 color : COLOR;
};

struct VSOut {
  float3 normal : TEXCOORD0;
  float4 color : COLOR;
  float4 light_pos : TEXCOORD1;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = mul(u.mvp, float4(i.pos, 1.0));
  o.normal = i.normal;
  o.color = i.color;
  o.light_pos = mul(u.light_mvp, float4(i.pos, 1.0));
  return o;
}
