// Bindings の test が共有する shader と描画先。
// draw は cell の画素を 1 つ塗る (xy = 画素、zw = target の幅と高さ)。
using Lub;

public static class BindingsTestUtil
{
    public const string Vs = @"
struct VsUniforms { float4 cell; };
ConstantBuffer<VsUniforms> vu;
static const float2 quad[6] = {float2(0, 0), float2(1, 0), float2(1, 1),
                               float2(0, 0), float2(1, 1), float2(0, 1)};
struct VSOut { float4 pos : SV_Position; };
[shader(""vertex"")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  float2 p = (vu.cell.xy + quad[vid]) / vu.cell.zw;
  VSOut o;
  o.pos = float4(p.x * 2.0 - 1.0, 1.0 - p.y * 2.0, 0.0, 1.0);
  return o;
}";

    public static DrawOpts Opts(ShaderRef sh)
    {
        return new DrawOpts { Shader = sh, Depth = false, Cull = Gfx.Cull.None };
    }

    public static TextureRef Target(string key, int w, int h)
    {
        return Gfx.UseTexture(key, w, h, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp })!;
    }

    public static void EmptyMainPass()
    {
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
    }
}
