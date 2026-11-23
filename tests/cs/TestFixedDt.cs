// Checks that --fixed-dt passes the given value as is to OnFrame(dt), and that Sys.ActualFps is also determined by the fixed dt (independent of the wall clock).
// The launch argument is --fixed-dt 0.0125 or 0.0166666666666667.
using System;
using Lub;

public static class TestFixedDt
{
    // The measured value appears after 1 second worth of frames (80 or 60) has accumulated
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
