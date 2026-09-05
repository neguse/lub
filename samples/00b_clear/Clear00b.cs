// lub の samples/00b_clear の entry。
// 実行: lub samples/00b_clear/Clear00b.csproj (transpile + watch + hot reload)
using System;
using static Lub;

public static class Clear00b
{
    static double t = 0;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "native";
        Lub.Config(new ConfigOpts { Backend = backend });
        Console.WriteLine("backend = " + backend);
        Console.WriteLine("clear demo");
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    public static void OnFrame(double dt)
    {
        t = t + dt;
        var r = 0.5 + 0.5 * Math.Sin(t);
        var g = 0.5 + 0.5 * Math.Sin(t + 2.0);
        var b = 0.5 + 0.5 * Math.Sin(t + 4.0);
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new double[] { r, g, b, 1.0 },
        });
        Gfx.EndPass();
    }
}
