// uniform block を持たないので、test_draw_state_reuse_other.vs.slang と組むと uniform block が 1 つの shader になる。
LUB_TEXTURE2D(tex);

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return float4(LUB_SAMPLE(tex, float2(0.5, 0.5)).r, 0.0, 0.0, 1.0);
}
