// Final present: copy a work texture to the swapchain.
LUB_TEXTURE2D(scene);
struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return float4(LUB_SAMPLE_LOD(scene, i.uv).rgb, 1.0);
}
