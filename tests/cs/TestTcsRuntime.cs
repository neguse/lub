// tcs の Lua 出力が呼ぶ global の tcs_rt を player が登録していること。
// float の既定の文字列化は .NET の float.ToString() と同じ文字列、string の Sort は .NET の ordinal の順になる。
using System;
using System.Collections.Generic;
using Lub;

public static class TestTcsRuntime
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        float zero = 0f;
        var text = 0.1f + " " + 1f / zero + " " + 4294967296f;
        Check.That(text == "0.1 Infinity 4.2949673E+09", "float to string: " + text);
        var names = new List<string> { "b", "B", "a" };
        names.Sort();
        var order = string.Join(",", names);
        Check.That(order == "B,a,b", "string sort: " + order);
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
