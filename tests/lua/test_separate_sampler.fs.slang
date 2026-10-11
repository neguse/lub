// HLSL の普通の書き方: texture と sampler を別に宣言する。sampler は宣言順で
// texture と組になる。u = 1.25 は left の REPEAT なら texel 0、right の CLAMP
// なら texel 1 に落ちるので、各 texture が自分の sampler で引けたかが分かる。
Texture2D left;
SamplerState left_sampler;
Texture2D right;
SamplerState right_sampler;

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 l = left.SampleLevel(left_sampler, float2(1.25, 0.5), 0.0);
  float4 r = right.SampleLevel(right_sampler, float2(1.25, 0.5), 0.0);
  return i.uv.x < 0.5 ? l : r;
}
