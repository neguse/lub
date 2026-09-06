// lub の samples/00c_buffer の entry。
// 実行: lub samples/00c_buffer/Buffer00c.csproj (transpile + watch + hot reload)
using System;
using System.Collections.Generic;
using static Lub;

public static class Buffer00c
{
    static List<float> data = new List<float>
    {
         0.0f,  0.5f, 0.0f,
        -0.5f, -0.5f, 0.0f,
         0.5f, -0.5f, 0.0f,
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

    public static void OnFrame(float dt)
    {
        var b = Gfx.UseBuffer("tri", Gfx.BufferType.Vertex, data, 1);
        if (b != null)
        {
            // buffer registered
        }
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.1f, 0.1f, 0.2f, 1.0f },
        });
        Gfx.EndPass();
    }
}
