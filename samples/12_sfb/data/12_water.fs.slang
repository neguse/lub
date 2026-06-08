// Analytic water plane composited over the beauty buffer. For each pixel we
// intersect the view ray with the world plane y = waterY; if that hit is in
// front of the scene geometry, the pixel is underwater surface and we shade it
// with flow-mapped ripples, refraction (uv offset), shoreline foam, and a
// screen-space reflection (SSR) that marches the reflected ray through the
// depth buffer so real objects show up in the water.
LUB_TEXTURE2D(scene);
LUB_TEXTURE2D(gpos);
LUB_TEXTURE2D(flowmap);
LUB_TEXTURE2D(waternormal);

static const float3 WATER_TINT = float3(0.10, 0.28, 0.34);
static const float3 SKY_TINT = float3(0.45, 0.62, 0.80);
static const float TILE = 0.35;

// SSR marching params.
static const int SSR_STEPS = 18;
static const float SSR_STEP = 0.18;
static const float SSR_THICKNESS = 0.4;

struct FSIn {
  float2 uv : TEXCOORD0;
  float4 a : TEXCOORD1; // inv-view row 0 (world.x from a view point)
  float4 b : TEXCOORD2; // row 1 (world.y)
  float4 c : TEXCOORD3; // row 2 (world.z)
  float4 p : TEXCOORD4; // x=time, y=waterY, z=proj00, w=proj11
};

float3 ripple_normal(float2 wuv, float2 flow, float t) {
  // flow mapping: scroll the ripple uv along the flow vector, cross-fade two
  // half-phase-offset samples so the scroll never visibly resets.
  float phase = frac(t * 0.4);
  float blend = abs(phase * 2.0 - 1.0);
  float3 nA =
      LUB_SAMPLE_LOD(waternormal, wuv + flow * (phase) * 0.4).xyz * 2.0 - 1.0;
  float3 nB =
      LUB_SAMPLE_LOD(waternormal, wuv + flow * (phase - 1.0) * 0.4).xyz * 2.0 -
      1.0;
  return normalize(lerp(nA, nB, blend));
}

// March the reflected ray through view space, projecting each step to uv and
// testing the depth buffer. Returns rgb + hit flag in .a. Sampling stays at
// uniform control flow (fixed loop, no early break) so implicit-LOD is valid.
float4 ssr(float3 start, float3 R, float2 proj) {
  float3 hitCol = float3(0.0, 0.0, 0.0);
  float hit = 0.0;
  float3 p = start;
  for (int k = 0; k < SSR_STEPS; ++k) {
    p += R * SSR_STEP;
    float pz = max(p.z, 0.001);
    float2 suv = float2(proj.x * p.x / pz, proj.y * p.y / pz) * 0.5 + 0.5;
    suv.y = 1.0 - suv.y; // G-buffer offscreen (y-down) vs sample uv
    float2 csuv = clamp(suv, 0.0, 1.0);
    float sceneZ = LUB_SAMPLE_LOD(gpos, csuv).z;
    float3 sceneCol = LUB_SAMPLE_LOD(scene, csuv).rgb;
    float diff = p.z - sceneZ;
    bool inb = suv.x >= 0.0 && suv.x <= 1.0 && suv.y >= 0.0 && suv.y <= 1.0;
    bool valid = inb && p.z > 0.05 && sceneZ > 0.0001 && diff > 0.02 &&
                 diff < SSR_THICKNESS;
    if (hit < 0.5 && valid) {
      hitCol = sceneCol;
      hit = 1.0;
    }
  }
  return float4(hitCol, hit);
}

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float3 sceneColor = LUB_SAMPLE_LOD(scene, i.uv).rgb;
  float4 P = LUB_SAMPLE_LOD(gpos, i.uv);
  if (P.z <= 0.0001)
    return float4(sceneColor, 1.0); // sky

  float waterY = i.p.y;
  float3 dir = normalize(P.xyz);   // view-space ray to this pixel
  float denom = dot(i.b.xyz, dir); // d(world.y)/d(t) along the ray
  if (abs(denom) < 1e-4)
    return float4(sceneColor, 1.0);
  float s = (waterY - i.b.w) / denom; // ray param where world.y == waterY
  float sceneDist = length(P.xyz);
  if (s <= 0.0 || s >= sceneDist)
    return float4(sceneColor, 1.0); // no water here

  float3 wView = dir * s;                 // water hit in view space
  float wx = dot(i.a.xyz, wView) + i.a.w; // world x/z for map lookup
  float wz = dot(i.c.xyz, wView) + i.c.w;
  float2 wuv = float2(wx, wz) * TILE;

  float2 flow = LUB_SAMPLE_LOD(flowmap, wuv).xy * 2.0 - 1.0;
  float3 wn = ripple_normal(wuv, flow, i.p.x);

  // view-space TBN of the water plane (world up/x/z expressed in view space).
  float3 Nv = normalize(i.b.xyz);
  float3 Tv = normalize(i.a.xyz);
  float3 Bv = normalize(i.c.xyz);
  float3 Np = normalize(Nv + (Tv * wn.x + Bv * wn.y) * 0.55);

  // refraction: offset the (submerged) scene sample by the ripple normal
  float3 refr = LUB_SAMPLE_LOD(scene, i.uv + wn.xy * 0.025).rgb;

  // shoreline foam where the scene sits just under the surface
  float depthDiff = sceneDist - s;
  float foam =
      saturate(1.0 - depthDiff / 0.22) * smoothstep(0.0, 0.04, depthDiff);

  // screen-space reflection of the real scene; sky tint where the ray misses.
  float3 R = reflect(dir, Np);
  float4 refl = ssr(wView, R, i.p.zw);
  float3 reflColor = refl.a > 0.5 ? refl.rgb : SKY_TINT;

  // fresnel between tinted refraction and the reflection
  float fres = pow(1.0 - saturate(dot(-dir, Np)), 4.0);
  float3 water =
      lerp(refr * 0.7 + WATER_TINT * 0.55, reflColor, saturate(fres));
  water = lerp(water, float3(0.95, 0.97, 1.0), foam);

  return float4(water, 1.0);
}
