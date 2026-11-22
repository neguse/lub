// Formatting and sorting of the tcs runtime in the player.
// The default string conversion of float and the formatting of int produce the same strings as .NET ToString, and Sort of string follows the .NET ordinal order.
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
        int count = 1234;
        var formatted = $"{count:D6} {count:N0}";
        Check.That(formatted == "001234 1,234", "int format: " + formatted);
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
