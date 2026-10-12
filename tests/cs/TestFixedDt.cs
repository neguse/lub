// --fixed-dt が指定値をそのまま OnFrame(dt) へ渡すことを確認する。
// 起動引数は --fixed-dt 0.0125。
using System;
using Lub;

public static class TestFixedDt
{
    const float expectedDt = 0.0125f;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        Check.That(Math.Abs(dt - expectedDt) <= 1e-15f, "expected " + expectedDt + ", got " + dt);
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
