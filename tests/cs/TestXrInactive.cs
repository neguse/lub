// The Xr surface on a backend without an XR session.
// Active / Focused return false, View / Input return null, and an invalid eye is an error.
// A pass to MainTex can begin as usual.
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
        // Advance the stage before a call that should fail (when it fails, OnFrame ends there)
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
