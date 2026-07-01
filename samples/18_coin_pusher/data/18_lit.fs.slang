struct FSIn {
  float3 normal : NORMAL;
  float4 color : COLOR;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float3 n = normalize(i.normal);
  float3 lightDir = normalize(float3(-0.4, 1.0, -0.55));
  float diffuse = max(dot(n, lightDir), 0.0);
  float ambient = 0.35;
  float3 lit = i.color.rgb * (ambient + diffuse * 0.75);
  return float4(lit, i.color.a);
}
