// Per-pass GPU time and draw count while Sys.ProfileLines is counting (Sys.TakeGpuPasses).
// Every frame draws a pass that issues 2 draws to MainTex, and checks that from 6 frames after counting starts, a pass with target main and 2 draws arrives.
// The time is 0 or more on backends that can measure it (vulkan, d3d12, webgpu with timestamp-query) and -1 on backends that cannot, and it prints which one as "gpu pass time: timed" / "untimed" (CI checks timed on d3d12 and web).
// Even on backends that can measure, passes of frames processed before their timestamps arrive come as -1 (more of them the further the GPU lags), so wait up to waitFrames until a measured pass arrives.
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

    const int waitFrames = 60;
    static int frame;
    static int timed;
    static int untimed;
    static string got = "";

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
        if (timed == 0 && frame < waitFrames)
            return;
        Sys.ProfileLines(false);
        if (timed + untimed == 0)
        {
            Console.WriteLine("FAIL expected passes into main with 2 draws: " + got);
            App.Quit(1);
            return;
        }
        Console.WriteLine("gpu pass time: " + (timed > 0 ? "timed" : "untimed") + " (" + got + ")");
        Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
