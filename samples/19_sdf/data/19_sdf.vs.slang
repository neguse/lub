struct Uniforms {
  float4x4 mvp;
  float4x4 model;
  float4x4 view;
  float4 params; // x = metal override (0..1)
  float4x4 bones[8];
};
ConstantBuffer<Uniforms> u;
struct VSIn {
  // NOTE: slang の WGSL 出力は TEXCOORDn を @location(n) に割り当てるので、
  // TEXCOORD の番号は宣言位置に合わせる (ズレると wasm で attr が崩れる)
  float3 pos : POSITION;
  float3 normal : NORMAL;
  float3 color : COLOR;
  float2 mr : TEXCOORD3;   // metallic, roughness (頂点に焼いた材質)
  float4 skin : TEXCOORD4; // j0, w0, j1, w1 (SDF 部位距離から焼いた重み)
};
struct VSOut {
  float4 pos : SV_Position;
  float3 normal : TEXCOORD2;  // world
  float3 vnormal : TEXCOORD3; // view space (matcap 用)
  float3 color : COLOR0;
  float2 mr : TEXCOORD4;
};
[shader("vertex")] VSOut vs_main(VSIn i) {
  VSOut o;
  // 2-bone LBS (model 空間で変形してから mvp)
  int j0 = int(i.skin.x);
  int j1 = int(i.skin.z);
  float4 p4 = float4(i.pos, 1.0);
  float3 sp =
      (mul(u.bones[j0], p4) * i.skin.y + mul(u.bones[j1], p4) * i.skin.w).xyz;
  float3 sn = mul((float3x3)u.bones[j0], i.normal) * i.skin.y +
              mul((float3x3)u.bones[j1], i.normal) * i.skin.w;
  o.pos = mul(u.mvp, float4(sp, 1.0));
  float3 wn = mul((float3x3)u.model, sn);
  o.normal = wn;
  o.vnormal = mul((float3x3)u.view, wn);
  o.color = i.color;
  o.mr = i.mr;
  return o;
}
