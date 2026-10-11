// HLSL の普通の書き方: texture と sampler を別に宣言する。名前が texture 名で
// 始まる sampler は、宣言の順によらずその texture と組になる(right_sampler は
// texture より前、left_sampler より前に宣言してある)。lut は Load だけで読み、
// sampler を持たない。u = 1.25 は left の REPEAT なら texel 0、right の CLAMP
// なら texel 1 に落ちるので、各 texture が自分の sampler で引けたかが分かる。
SamplerState right_sampler;
Texture2D left;
Texture2D right;
SamplerState left_sampler;
Texture2D lut;

struct FSIn {
  float2 uv : TEXCOORD0;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 l = left.SampleLevel(left_sampler, float2(1.25, 0.5), 0.0);
  float4 r = right.SampleLevel(right_sampler, float2(1.25, 0.5), 0.0);
  return (i.uv.x < 0.5 ? l : r) * lut.Load(int3(0, 0, 0));
}
