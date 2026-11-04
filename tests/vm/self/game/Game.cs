using Lub;

// 板 1 枚。自分の Draw で描く
public class Crate
{
    public float X0;
    public float X1;
    public int Spins;

    public void Draw(ShaderRef sh)
    {
        var b = new Bindings();
        b.Set("rect", new Vec4(X0, -1, X1, 1));
        Gfx.Draw(4, b, new DrawOpts { Shader = sh, Primitive = Gfx.Primitive.TriangleStrip, Cull = Gfx.Cull.None });
    }

    public void Spin()
    {
        Spins = Spins + 1;
    }
}

public static class Game
{
    const string Vs = "struct U { float4 rect; };\n"
        + "ConstantBuffer<U> u;\n"
        + "struct VSOut { float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  VSOut o;\n"
        + "  o.pos = float4(lerp(u.rect.x, u.rect.z, c.x), lerp(u.rect.y, u.rect.w, c.y), 0.5, 1.0);\n"
        + "  return o;\n"
        + "}\n";
    const string Fs = "[shader(\"fragment\")] float4 fs_main() : SV_Target { return float4(0.5, 0.5, 0.5, 1.0); }\n";

    public static Crate Left = new Crate { X0 = -1, X1 = 0 };
    public static Crate Right = new Crate { X0 = 0, X1 = 1 };

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        var sh = Gfx.UseShader("test_self", Vs, Fs, 1);
        if (sh == null)
            return;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        Left.Draw(sh);
        Right.Draw(sh);
        Gfx.EndPass();
        Right.Spin();
    }
}
