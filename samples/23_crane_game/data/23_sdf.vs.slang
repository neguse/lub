struct Uniforms {
  float4x4 mvp;
  float4x4 model;
};
ConstantBuffer<Uniforms> u;
struct VSIn {
  // NOTE: slang の WGSL 出力は TEXCOORDn を @location(n) に割り当てるので、
  // TEXCOORD の番号は宣言位置に合わせる (ズレると wasm で attr が崩れる)
  float3 pos : POSITION;
  float3 normal : NORMAL;
  float3 color : COLOR;
  float2 mr : TEXCOORD3; // metallic, roughness (頂点に焼いた材質)
};
struct VSOut {
  float3 normal : TEXCOORD2; // world
  float3 color : COLOR0;
  float2 mr : TEXCOORD3;
  float4 pos : SV_Position;
};
[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  o.pos = mul(u.mvp, float4(i.pos, 1.0));
  // Rigid transforms only, so model * (n, 0) is exact for normals.
  o.normal = mul(u.model, float4(i.normal, 0.0)).xyz;
  o.color = i.color;
  o.mr = i.mr;
  return o;
}
