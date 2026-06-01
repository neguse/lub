// Analytic water plane composited over the beauty buffer. For each pixel we
// intersect the view ray with the world plane y = waterY; if that hit is in
// front of the scene geometry, the pixel is underwater surface and we shade it
// with flow-mapped ripples, refraction (uv offset), a fresnel sky reflection,
// and shoreline foam (from the water/scene depth difference).
LUB_TEXTURE2D(scene);
LUB_TEXTURE2D(gpos);
LUB_TEXTURE2D(flowmap);
LUB_TEXTURE2D(waternormal);

static const float3 WATER_TINT = float3(0.10, 0.28, 0.34);
static const float3 SKY_TINT = float3(0.45, 0.62, 0.80);
static const float TILE = 0.35;

struct FSIn {
    float2 uv : TEXCOORD0;
    float4 a : TEXCOORD1; // inv-view row 0
    float4 b : TEXCOORD2; // row 1
    float4 c : TEXCOORD3; // row 2
    float4 p : TEXCOORD4; // x=time, y=waterY
};

float3 ripple_normal(float2 wuv, float2 flow, float t) {
    // flow mapping: scroll the ripple uv along the flow vector, cross-fade two
    // half-phase-offset samples so the scroll never visibly resets.
    float phase = frac(t * 0.4);
    float blend = abs(phase * 2.0 - 1.0);
    float3 nA = LUB_SAMPLE(waternormal, wuv + flow * (phase) * 0.4).xyz * 2.0 - 1.0;
    float3 nB = LUB_SAMPLE(waternormal, wuv + flow * (phase - 1.0) * 0.4).xyz * 2.0 - 1.0;
    return normalize(lerp(nA, nB, blend));
}

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    float3 sceneColor = LUB_SAMPLE(scene, i.uv).rgb;
    float4 P = LUB_SAMPLE(gpos, i.uv);
    if (P.z <= 0.0001) return float4(sceneColor, 1.0); // sky

    float waterY = i.p.y;
    float3 dir = normalize(P.xyz);            // view-space ray to this pixel
    float denom = dot(i.b.xyz, dir);          // d(world.y)/d(t) along the ray
    if (abs(denom) < 1e-4) return float4(sceneColor, 1.0);
    float s = (waterY - i.b.w) / denom;       // ray param where world.y == waterY
    float sceneDist = length(P.xyz);
    if (s <= 0.0 || s >= sceneDist) return float4(sceneColor, 1.0); // no water here

    float3 wView = dir * s;                    // water hit in view space
    float wx = dot(i.a.xyz, wView) + i.a.w;    // world x/z for map lookup
    float wz = dot(i.c.xyz, wView) + i.c.w;
    float2 wuv = float2(wx, wz) * TILE;

    float2 flow = LUB_SAMPLE(flowmap, wuv).xy * 2.0 - 1.0;
    float3 wn = ripple_normal(wuv, flow, i.p.x);

    // refraction: offset the (submerged) scene sample by the ripple normal
    float3 refr = LUB_SAMPLE(scene, i.uv + wn.xy * 0.025).rgb;

    // shoreline foam where the scene sits just under the surface
    float depthDiff = sceneDist - s;
    float foam = saturate(1.0 - depthDiff / 0.22) * smoothstep(0.0, 0.04, depthDiff);

    // fresnel between tinted refraction and a sky reflection
    float ndv = saturate(dot(wn, float3(0.0, 0.0, 1.0)));
    float fres = pow(1.0 - ndv, 3.0);
    float3 water = lerp(refr * 0.7 + WATER_TINT * 0.6, SKY_TINT, saturate(fres));
    water = lerp(water, float3(0.95, 0.97, 1.0), foam);

    return float4(water, 1.0);
}
