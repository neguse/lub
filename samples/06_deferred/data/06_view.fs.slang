// Single-texture fullscreen view. Sample 6 reuses this shader twice — once
// per G-buffer target — to demonstrate that the MRT pass wrote distinct
// pixels into each attachment.
LUB_TEXTURE2D(gbuf);
struct FSIn { float2 uv : TEXCOORD0; };
[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    return LUB_SAMPLE(gbuf, i.uv);
}
