// draw が読む col に赤を書く。
RWStructuredBuffer<float4> col;

[shader("compute")][numthreads(1, 1, 1)] void cs_main(uint3 tid
                                                      : SV_DispatchThreadID) {
  col[0] = float4(1.0, 0.0, 0.0, 1.0);
}
