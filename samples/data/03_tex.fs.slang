Texture2D    diffuse;
SamplerState diffuse_smp;
struct FSIn { float2 uv : TEXCOORD0; };
[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    return diffuse.Sample(diffuse_smp, i.uv);
}
