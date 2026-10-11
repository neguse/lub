// 1 つの sampler を 2 つの texture で共有する。lub は texture ごとに sampler を
// 束縛するので表せず、shader compile が error になる。
Texture2D first;
Texture2D second;
SamplerState shared;

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return first.SampleLevel(shared, i.uv, 0.0) +
         second.SampleLevel(shared, i.uv, 0.0);
}
