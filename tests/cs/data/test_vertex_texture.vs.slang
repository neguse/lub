// A vertex shader samples a texture (neguse/lub#68). Each vertex samples tex
// with its own uv and passes it to the fragment as a color. The fragment does
// not touch the texture.
LUB_TEXTURE2D(tex);

struct V {
  float2 pos;
  float2 uv;
};
StructuredBuffer<V> verts;

struct VSOut {
  float4 pos : SV_Position;
  float4 col : COLOR0;
};

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V i = verts[vid];
  VSOut o;
  o.pos = float4(i.pos, 0.0, 1.0);
  o.col = LUB_SAMPLE_LOD(tex, i.uv);
  return o;
}
