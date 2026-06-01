// G-buffer geometry pass FS. Writes three render targets:
//   SV_Target0 gColor    : Blinn-Phong lit color (rgb), a = emissive mask
//   SV_Target1 gNormal   : view-space normal, encoded *0.5+0.5
//   SV_Target2 gPosition : view-space position (xyz), w = linear depth (vpos.z)
// View space here is left-handed (camera looks down +Z), so vpos.z grows with
// distance and the background (cleared to z=0) reads as "no geometry".
struct FSIn {
    float3 vpos : TEXCOORD0;
    float3 vn : TEXCOORD1;
    float3 ldir : TEXCOORD2;
    float4 color : COLOR;
};

struct FSOut {
    float4 gColor : SV_Target0;
    float4 gNormal : SV_Target1;
    float4 gPosition : SV_Target2;
};

[shader("fragment")]
FSOut fs_main(FSIn i) {
    float3 N = normalize(i.vn);
    float3 L = normalize(i.ldir);
    float3 V = normalize(-i.vpos);           // camera at origin in view space
    float3 H = normalize(L + V);

    float ndl = saturate(dot(N, L));
    float ndh = saturate(dot(N, H));
    float spec = pow(ndh, 48.0) * 0.45;
    float ambient = 0.22;

    float3 lit = i.color.rgb * (ambient + ndl * 0.9) + spec;

    FSOut o;
    o.gColor = float4(lit, i.color.a);
    o.gNormal = float4(N * 0.5 + 0.5, 1.0);
    o.gPosition = float4(i.vpos, i.vpos.z);
    return o;
}
