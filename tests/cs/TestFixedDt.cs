// --fixed-dt が指定値をそのまま OnFrame(dt) へ渡し、Sys.ActualFps も fixed dt から決まる (壁時計に依らない) ことを確認する。
// 起動引数は --fixed-dt 0.0125 か 0.0166666666666667。
using System;
using Lub;

public static class TestFixedDt
{
    // 実測は 1 秒分の frame (80 か 60) を積んでから出る
    const int fpsFrames = 100;
    static int frame;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        var expectedDt = dt < 0.015f ? 0.0125f : 0.0166666666666667f;
        var expectedFps = dt < 0.015f ? 80.0f : 60.0f;
        Check.That(Math.Abs(dt - expectedDt) <= 1e-15f, "expected " + expectedDt + ", got " + dt);
        frame++;
        if (frame < fpsFrames)
            return;
        Check.That(Sys.ActualFps() == expectedFps, "fps must follow the fixed dt, got " + Sys.ActualFps());
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
