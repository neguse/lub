// lub の samples/00_hello (Haxe 版 Hello00.hx) の TinyC# 版 entry。
// 実行: lub samples/00_hello/Hello00.csproj (transpile + watch + hot reload)
using System;

public static class Hello00
{
    public static void onInit()
    {
        Console.WriteLine("[lua] onInit");
        var backend = os.getenv("LUB_BACKEND") ?? "native";
        Lub.config(new ConfigOpts { backend = backend });
        Console.WriteLine("config called");
        Console.WriteLine("VERTEX=" + Gfx.VERTEX + " RGBA8=" + Gfx.RGBA8
            + " CLEAR=" + Gfx.CLEAR);
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onFrame(double dt)
    {
        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new double[] { 0.1, 0.1, 0.2, 1.0 },
        });
        Gfx.end_pass();
    }

    public static void onQuit()
    {
        Console.WriteLine("[lua] onQuit");
    }
}
