// lub の samples/00c_buffer (Haxe 版 Buffer00c.hx) の TinyC# 版 entry。
// 実行: lub samples/00c_buffer/Buffer00c.csproj (transpile + watch + hot reload)
using System;
using System.Collections.Generic;

public static class Buffer00c
{
    static List<float> data = new List<float>
    {
         0.0f,  0.5f, 0.0f,
        -0.5f, -0.5f, 0.0f,
         0.5f, -0.5f, 0.0f,
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

    public static void onFrame(float dt)
    {
        var b = Gfx.use_buffer("tri", Gfx.VERTEX, data, 1);
        if (b != null)
        {
            // buffer registered
        }
        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new float[] { 0.1f, 0.1f, 0.2f, 1.0f },
        });
        Gfx.end_pass();
    }
}
