// ../TestPick.cs が lider で開くゲーム。画面の中央に板を 1 枚描く。
// 板は Quad の個体 (Shown) が自分の Draw で描く。
// Gfx.Draw の行番号を ../TestPick.cs の DrawLine が見るので、行を足したら合わせる。
using Lub;

// 板 1 枚。自分の Draw で描く
public class Quad
{
    public void Draw(ShaderRef sh)
    {
        Gfx.Draw(4, new Bindings(), new DrawOpts { Shader = sh, Primitive = Gfx.Primitive.TriangleStrip, Cull = Gfx.Cull.None });
    }
}

public static class Game
{
    const string Vs = "[shader(\"vertex\")] float4 vs_main(uint vid : LUB_VERTEX_ID) : SV_Position {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  return float4(c.x - 0.5, c.y - 0.5, 0.5, 1.0);\n"
        + "}\n";
    const string Fs = "[shader(\"fragment\")] float4 fs_main() : SV_Target { return float4(1, 0.8, 0.2, 1); }\n";
    static Quad Shown = new Quad();

    public static void OnFrame(float dt)
    {
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        var sh = Gfx.UseShader("test_pick_quad", Vs, Fs, 1);
        if (sh != null)
            Shown.Draw(sh);
        Gfx.EndPass();
    }
}
