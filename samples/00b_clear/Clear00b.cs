// lub の samples/00b_clear の entry。
// 実行: lub samples/00b_clear/Clear00b.csproj (transpile + watch + hot reload)
using System;
using static Lub;

public static class Clear00b
{
    static float t = 0;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
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

    public static void OnFrame(float dt)
    {
        t = t + dt;
        var r = 0.5f + 0.5f * (float)Math.Sin(t);
        var g = 0.5f + 0.5f * (float)Math.Sin(t + 2.0f);
        var b = 0.5f + 0.5f * (float)Math.Sin(t + 4.0f);
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { r, g, b, 1.0f },
        });
        Gfx.EndPass();
    }
}
