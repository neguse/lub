// Screen-space "toy" effects, selected by the mode forwarded from the VS.
//   0 passthrough   1 posterize   2 pixelize   3 chromatic aberration
//   4 sharpen       5 dilation    6 normal      7 depth
// All texture sampling is at uniform control flow; mode only selects between
// precomputed results (sampling inside a divergent branch makes implicit-LOD
// derivatives undefined and crashes some drivers). Texel baked for 1280x720.
LUB_TEXTURE2D(scene);

static const float2 TEXEL = float2(1.0 / 1280.0, 1.0 / 720.0);

struct FSIn { float2 uv : TEXCOORD0; float2 info : TEXCOORD1; };

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    int mode = int(i.info.x + 0.5);
    float amount = i.info.y;
    float2 t = TEXEL;
    float2 uv = i.uv;

    float4 center = LUB_SAMPLE_LOD(scene, uv);
    float3 base = center.rgb;

    float levels = 5.0;
    float3 cPost = floor(base * levels) / (levels - 1.0);

    float px = 96.0;
    float3 cPix = LUB_SAMPLE_LOD(scene, (floor(uv * px) + 0.5) / px).rgb;

    float2 dir = uv - 0.5;
    float3 cChrom = float3(
        LUB_SAMPLE_LOD(scene, uv + dir * amount).r,
        base.g,
        LUB_SAMPLE_LOD(scene, uv - dir * amount).b);

    float3 sR = LUB_SAMPLE_LOD(scene, uv + float2( t.x, 0.0)).rgb;
    float3 sL = LUB_SAMPLE_LOD(scene, uv + float2(-t.x, 0.0)).rgb;
    float3 sU = LUB_SAMPLE_LOD(scene, uv + float2(0.0,  t.y)).rgb;
    float3 sD = LUB_SAMPLE_LOD(scene, uv + float2(0.0, -t.y)).rgb;
    float3 sRU = LUB_SAMPLE_LOD(scene, uv + float2( t.x,  t.y)).rgb;
    float3 sLU = LUB_SAMPLE_LOD(scene, uv + float2(-t.x,  t.y)).rgb;
    float3 sRD = LUB_SAMPLE_LOD(scene, uv + float2( t.x, -t.y)).rgb;
    float3 sLD = LUB_SAMPLE_LOD(scene, uv + float2(-t.x, -t.y)).rgb;

    float3 cSharp = saturate(base * 5.0 - sR - sL - sU - sD);
    float3 cDil = max(max(max(max(base, sR), sL), sU), sD);
    cDil = max(max(max(max(cDil, sRU), sLU), sRD), sLD);

    float dv = saturate(center.w / 14.0);
    float3 cDepth = float3(dv, dv, dv);

    float3 c = base;
    if (mode == 1) c = cPost;
    else if (mode == 2) c = cPix;
    else if (mode == 3) c = cChrom;
    else if (mode == 4) c = cSharp;
    else if (mode == 5) c = cDil;
    else if (mode == 7) c = cDepth;

    return float4(c, 1.0);
}
