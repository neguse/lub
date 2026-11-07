using System.Collections.Generic;
using Lub;

public static class Game
{
    public static List<int>? Keep;
    public static int[]? Big;
    static int sum;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        Keep = new List<int> { 1, 2, 3 };
        for (int i = 0; i < 200000; i++)
            sum += i;
        Big = new int[100000];
        var sh = Gfx.UseShader("profile", Vs, Fs, 1);
        if (sh == null)
            return;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        var b = new Bindings();
        b.Set("rect", new Vec4(-1, -1, 1, 1));
        Gfx.Draw(4, b, new DrawOpts { Shader = sh, Primitive = Gfx.Primitive.TriangleStrip, Cull = Gfx.Cull.None });
        Gfx.Draw(4, b, new DrawOpts { Shader = sh, Primitive = Gfx.Primitive.TriangleStrip, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

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
}
