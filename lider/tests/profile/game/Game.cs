// The game ../TestProfile.cs opens in lider. Every frame it repeats additions, makes an array and puts it in a static (Game.keep), and emits two draws in a pass to MainTex.
// ../TestProfile.cs looks at the line numbers, so keep them in sync if you add lines.
using Lub;

public static class Game
{
    static int[]? keep;
    static int sum;

    public static void OnFrame(float dt)
    {
        for (int i = 0; i < 200000; i++)
            sum += i;
        keep = new int[1000];
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
        + "[shader(\"vertex\")] float4 vs_main(uint vid : LUB_VERTEX_ID) : SV_Position {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  return float4(lerp(u.rect.x, u.rect.z, c.x), lerp(u.rect.y, u.rect.w, c.y), 0.5, 1.0);\n"
        + "}\n";
    const string Fs = "[shader(\"fragment\")] float4 fs_main() : SV_Target { return float4(0.1, 0.1, 0.1, 1); }\n";
}
