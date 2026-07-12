// lub の samples/00b_clear (Haxe 版 Clear00b.hx) の TinyC# 版 entry。
// 実行: lub samples/00b_clear/Clear00b.csproj (transpile + watch + hot reload)
using System;

public static class Clear00b
{
    static double t = 0;

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND") ?? "native";
        Lub.config(new ConfigOpts { backend = backend });
        Console.WriteLine("backend = " + backend);
        Console.WriteLine("clear demo");
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    public static void onFrame(double dt)
    {
        t = t + 1.0 / 60.0;
        var r = 0.5 + 0.5 * Math.Sin(t);
        var g = 0.5 + 0.5 * Math.Sin(t + 2.0);
        var b = 0.5 + 0.5 * Math.Sin(t + 4.0);
        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new double[] { r, g, b, 1.0 },
        });
        Gfx.end_pass();
    }
}
