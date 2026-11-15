// ../TestWhatis.cs が lider で開くゲーム。画面の真ん中に四角を 1 つ描く。
using Lub;

public static class Game
{
    const string Vs = "[shader(\"vertex\")] float4 vs_main(uint vid : LUB_VERTEX_ID) : SV_Position {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  return float4(c.x - 0.5, c.y - 0.5, 0.5, 1.0);\n"
        + "}\n";
    const string Fs = "[shader(\"fragment\")] float4 fs_main() : SV_Target { return float4(1, 0.8, 0.2, 1); }\n";

    public static void OnFrame(float dt)
    {
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        Quad();
        Gfx.EndPass();
    }

    static void Quad()
    {
        var sh = Gfx.UseShader("test_whatis_quad", Vs, Fs, 1);
        if (sh == null)
            return;
        Gfx.Draw(4, new Bindings(), new DrawOpts { Shader = sh, Primitive = Gfx.Primitive.TriangleStrip, Cull = Gfx.Cull.None });
    }
}
