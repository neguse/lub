// Camera motion blur. Reprojects each pixel's view-space position with last
// frame's view-projection to find where it was on screen, then smears along
// that screen-space velocity. With a still camera the velocity is ~0 (no-op),
// which keeps the headless golden deterministic.
LUB_TEXTURE2D(scene);
LUB_TEXTURE2D(gpos);

static const int SAMPLES = 9;
static const float STRENGTH = 0.6;  // fraction of the per-frame velocity to smear
static const float MAX_UV = 0.05;   // clamp so fast motion doesn't streak forever

struct FSIn {
    float2 uv : TEXCOORD0;
    float4 m0 : TEXCOORD1;
    float4 m1 : TEXCOORD2;
    float4 m2 : TEXCOORD3;
    float4 m3 : TEXCOORD4;
};

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    float3 base = LUB_SAMPLE_LOD(scene, i.uv).rgb;
    float4 P = LUB_SAMPLE_LOD(gpos, i.uv);
    if (P.z <= 0.0001) return float4(base, 1.0);

    float4 vp = float4(P.xyz, 1.0);
    float4 prevClip = float4(dot(i.m0, vp), dot(i.m1, vp), dot(i.m2, vp), dot(i.m3, vp));
    if (abs(prevClip.w) < 1e-5) return float4(base, 1.0);
    float2 prevUV = prevClip.xy / prevClip.w * 0.5 + 0.5;
    // The G-buffer is rasterised y-down but the post quad samples y-up, so the
    // reprojected uv's y is flipped relative to this pass's uv.
    prevUV.y = 1.0 - prevUV.y;

    float2 vel = (i.uv - prevUV) * STRENGTH;
    float mag = length(vel);
    if (mag > MAX_UV) vel *= MAX_UV / mag;
    if (mag < 1e-5) return float4(base, 1.0);

    float3 acc = float3(0.0, 0.0, 0.0);
    for (int k = 0; k < SAMPLES; ++k) {
        float t = (float(k) / float(SAMPLES - 1)) - 0.5; // centred [-0.5, 0.5]
        acc += LUB_SAMPLE_LOD(scene, i.uv + vel * t).rgb;
    }
    return float4(acc / float(SAMPLES), 1.0);
}
