// lub の samples/00_hello (Haxe 版 Hello00.hx) の TinyC# 版 entry。
// 実行: lub samples/00_hello/Hello00.csproj (transpile + watch + hot reload)
using System;
using static Lub;

public static class Hello00
{
    public static void OnInit()
    {
        Console.WriteLine("[lua] onInit");
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "native";
        Lub.Config(new ConfigOpts { Backend = backend });
        Console.WriteLine("config called");
        Console.WriteLine("VERTEX=" + Gfx.BufferType.Vertex + " RGBA8=" + Gfx.PixelFormat.Rgba8
            + " CLEAR=" + Gfx.LoadAction.Clear);
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnFrame(double dt)
    {
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new double[] { 0.1, 0.1, 0.2, 1.0 },
        });
        Gfx.EndPass();
    }

    public static void OnQuit()
    {
        Console.WriteLine("[lua] onQuit");
    }
}
