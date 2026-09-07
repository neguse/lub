struct VSParams {
  float4 transform;
};
ConstantBuffer<VSParams> vu;

struct V {
  float2 pos;
  float2 uv;
};
StructuredBuffer<V> verts;
struct VSOut {
  float2 uv : TEXCOORD0;
  float4 pos : SV_Position;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V i = verts[vid];
  VSOut o;
  float2 p = i.pos * vu.transform.xy + vu.transform.zw;
  o.pos = float4(p, 0.0, 1.0);
  o.uv = i.uv;
  return o;
}
