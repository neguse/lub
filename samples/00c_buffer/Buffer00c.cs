// lub の samples/00c_buffer (Haxe 版 Buffer00c.hx) の TinyC# 版 entry。
// 実行: lub samples/00c_buffer/Buffer00c.csproj (transpile + watch + hot reload)
using System;
using System.Collections.Generic;
using static Lub;

public static class Buffer00c
{
    static List<double> data = new List<double>
    {
         0.0,  0.5, 0.0,
        -0.5, -0.5, 0.0,
         0.5, -0.5, 0.0,
    };

    public static void OnInit()
    {
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    public static void OnFrame(double dt)
    {
        var b = Gfx.UseBuffer("tri", Gfx.BufferType.Vertex, data, 1);
        if (b != null)
        {
            // buffer registered
        }
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new double[] { 0.1, 0.1, 0.2, 1.0 },
        });
        Gfx.EndPass();
    }
}
