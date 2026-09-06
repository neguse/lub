// lub の samples/00_hello の entry。
// 実行: lub samples/00_hello/Hello00.csproj (transpile + watch + hot reload)
using System;
using static Lub;

public static class Hello00
{
    public static void OnInit()
    {
        Console.WriteLine("[lua] onInit");
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        Lub.Config(new ConfigOpts { Backend = backend });
        Console.WriteLine("config called");
        Console.WriteLine("VERTEX=" + Gfx.BufferType.Vertex + " RGBA8=" + Gfx.PixelFormat.Rgba8
            + " CLEAR=" + Gfx.LoadAction.Clear);
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnFrame(float dt)
    {
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.1f, 0.1f, 0.2f, 1.0f },
        });
        Gfx.EndPass();
    }

    public static void OnQuit()
    {
        Console.WriteLine("[lua] onQuit");
    }
}
