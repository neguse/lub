// XR セッションが無い backend での Xr の面。
// Active / Focused は false、View / Input は null を返し、眼の指定が不正なら error になる。
// MainTex への pass は通常どおり begin できる。
using System;
using Lub;

public static class TestXrInactive
{
    static int step;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        // 段階は失敗するはずの呼び出しの前に進める (失敗すると OnFrame はそこで終わる)
        var s = step;
        step++;
        if (s == 0)
        {
            Check.That(Xr.Active() == false, "Xr.Active must be false");
            Check.That(Xr.Focused() == false, "Xr.Focused must be false");
            Check.That(Xr.View(0, 0.05f, 500) == null, "Xr.View(0) must be null");
            Check.That(Xr.View(1, 0.05f, 500) == null, "Xr.View(1) must be null");
            Check.That(Xr.Input(0) == null, "Xr.Input(0) must be null");
            Check.That(Xr.Input(1) == null, "Xr.Input(1) must be null");
            Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
            Gfx.EndPass();
        }
        else if (s == 1)
        {
            Check.Error("Xr.View with eye 2", "xr.view");
            Xr.View(2, 0.05f, 500);
        }
        else if (s == 2)
        {
            Check.Error("Xr.View with near 0", "xr.view");
            Xr.View(0, 0, 500);
        }
        else if (s == 3)
        {
            Check.Error("Xr.View with near > far", "xr.view");
            Xr.View(0, 500, 0.05f);
        }
        else if (s == 4)
        {
            Check.Error("Xr.Input with hand 2", "xr.input");
            Xr.Input(2);
        }
        else
        {
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
