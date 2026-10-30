// --fixed-dt が指定値をそのまま OnFrame(dt) へ渡し、Sys.ActualFps も fixed dt から決まる (壁時計に依らない) ことを確認する。
// 起動引数は --fixed-dt 0.0125。
using System;
using Lub;

public static class TestFixedDt
{
    const float expectedDt = 0.0125f;
    // 実測は 1 秒分の frame (80) を積んでから出る
    const int fpsFrames = 100;
    static int frame;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        Check.That(Math.Abs(dt - expectedDt) <= 1e-15f, "expected " + expectedDt + ", got " + dt);
        frame++;
        if (frame < fpsFrames)
            return;
        Check.That(Math.Abs(Sys.ActualFps() - 80.0f) <= 1e-3f, "fps must follow the fixed dt, got " + Sys.ActualFps());
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
