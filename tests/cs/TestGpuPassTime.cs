// Sys.ProfileLines で数えている間の、pass ごとの GPU の時間と draw の数 (Sys.TakeGpuPasses)。
// 毎 frame、MainTex へ draw を 2 つ出す pass を描き、数え始めてから 6 frame 後に、描き先 main、draw 2 つの pass が届くことを見る。
// 時間は、測れる backend (vulkan、d3d12) では 0 以上、測れない backend では -1 で、どちらだったかを "gpu pass time: timed" / "untimed" と出す (CI は d3d12 で timed を確かめる)。
using System;
using Lub;

public static class TestGpuPassTime
{
    const string Vs = "struct U { float4 rect; };\n"
        + "ConstantBuffer<U> u;\n"
        + "[shader(\"vertex\")] float4 vs_main(uint vid : LUB_VERTEX_ID) : SV_Position {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  return float4(lerp(u.rect.x, u.rect.z, c.x), lerp(u.rect.y, u.rect.w, c.y), 0.5, 1.0);\n"
        + "}\n";
    const string Fs = "[shader(\"fragment\")] float4 fs_main() : SV_Target { return float4(0.5, 0.5, 0.5, 1); }\n";

    static int frame;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    public static void OnFrame(float dt)
    {
        frame++;
        if (frame == 1)
            Sys.ProfileLines(true);
        var sh = Gfx.UseShader("gpu_pass_time", Vs, Fs, 1);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        var b = new Bindings();
        b.Set("rect", new Vec4(-0.5f, -0.5f, 0.5f, 0.5f));
        Gfx.Draw(4, b, new DrawOpts { Shader = sh!, Primitive = Gfx.Primitive.TriangleStrip, Cull = Gfx.Cull.None, Depth = false });
        Gfx.Draw(4, b, new DrawOpts { Shader = sh!, Primitive = Gfx.Primitive.TriangleStrip, Cull = Gfx.Cull.None, Depth = false });
        Gfx.EndPass();
        if (frame < 7)
            return;
        var timed = 0;
        var untimed = 0;
        var got = "";
        foreach (var p in Sys.TakeGpuPasses())
        {
            got += p.Target + " " + p.Ms + "ms " + p.Draws + " draws, ";
            if (p.Target != "main" || p.Draws != 2)
                continue;
            if (p.Ms >= 0)
                timed++;
            else
                untimed++;
        }
        Sys.ProfileLines(false);
        if (timed + untimed == 0 || (timed > 0 && untimed > 0))
        {
            Console.WriteLine("FAIL expected passes into main with 2 draws, all timed or all untimed: " + got);
            App.Quit(1);
            return;
        }
        Console.WriteLine("gpu pass time: " + (timed > 0 ? "timed" : "untimed") + " (" + got + ")");
        App.Quit(0);
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
