struct GeoUniforms {
  float4x4 proj;
  float4x4 view;
  float4x4 model;
  float4x4 light_mvp;
  float4 base_color_factor;
  float4 material;      // x metallic, y roughness, z alpha cutoff, w alpha mode
  float4 normal_params; // x normal scale
};
ConstantBuffer<GeoUniforms> u;

struct VSIn {
  float3 pos : POSITION;
  float3 normal : NORMAL;
  float2 uv : TEXCOORD0;
  float4 tangent : TANGENT;
};

struct VSOut {
  float3 vpos : TEXCOORD0;
  float3 vn : TEXCOORD1;
  float2 uv : TEXCOORD2;
  float4 vt : TEXCOORD3;
  float4 lpos : TEXCOORD4;
  float4 base_color_factor : TEXCOORD5;
  float4 material : TEXCOORD6;
  float4 normal_params : TEXCOORD7;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  float4 wp = mul(u.model, float4(i.pos, 1.0));
  float4 vp = mul(u.view, wp);
  o.vpos = vp.xyz;
  o.pos = mul(u.proj, vp);
  o.vn = mul((float3x3)u.view, mul((float3x3)u.model, i.normal));
  o.uv = i.uv;
  o.vt = float4(mul((float3x3)u.view, mul((float3x3)u.model, i.tangent.xyz)),
                i.tangent.w);
  o.lpos = mul(u.light_mvp, wp);
  o.base_color_factor = u.base_color_factor;
  o.material = u.material;
  o.normal_params = u.normal_params;
  return o;
}
