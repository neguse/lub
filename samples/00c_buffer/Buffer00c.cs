// lub の samples/00c_buffer (Haxe 版 Buffer00c.hx) の TinyC# 版 entry。
// 実行: lub samples/00c_buffer/Buffer00c.csproj (transpile + watch + hot reload)
using System;
using System.Collections.Generic;

public static class Buffer00c
{
    static List<double> data = new List<double>
    {
         0.0,  0.5, 0.0,
        -0.5, -0.5, 0.0,
         0.5, -0.5, 0.0,
    };

    public static void onInit()
    {
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    public static void onFrame(double dt)
    {
        var b = Gfx.use_buffer("tri", Gfx.VERTEX, data, 1);
        if (b != null)
        {
            // buffer registered
        }
        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new double[] { 0.1, 0.1, 0.2, 1.0 },
        });
        Gfx.end_pass();
    }
}
