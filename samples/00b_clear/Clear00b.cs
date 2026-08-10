// lub の samples/00b_clear (Haxe 版 Clear00b.hx) の TinyC# 版 entry。
// 実行: lub samples/00b_clear/Clear00b.csproj (transpile + watch + hot reload)
using System;

public static class Clear00b
{
    static float t = 0;

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND");
        Lub.config(new ConfigOpts { backend = backend });
        Console.WriteLine("backend = " + (backend ?? "(default)"));
        Console.WriteLine("clear demo");
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    public static void onFrame(float dt)
    {
        t = t + dt;
        var r = 0.5f + 0.5f * (float)Math.Sin(t);
        var g = 0.5f + 0.5f * (float)Math.Sin(t + 2.0f);
        var b = 0.5f + 0.5f * (float)Math.Sin(t + 4.0f);
        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new float[] { r, g, b, 1.0f },
        });
        Gfx.end_pass();
    }
}
