LUB_TEXTURE2D(base_color);

struct FSIn {
  float depth : TEXCOORD0;
  float2 uv : TEXCOORD1;
  float4 base_color_factor : TEXCOORD2;
  float4 material : TEXCOORD3;
};

float3 pack_depth(float v) {
  float3 enc = frac(v * float3(1.0, 255.0, 65025.0));
  enc -= enc.yzz * float3(1.0 / 255.0, 1.0 / 255.0, 0.0);
  return enc;
}

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 base = LUB_SAMPLE_LOD(base_color, i.uv) * i.base_color_factor;
  if (i.material.w > 0.5 && base.a < i.material.z) {
    discard;
  }
  return float4(pack_depth(saturate(i.depth)), 1.0);
}
