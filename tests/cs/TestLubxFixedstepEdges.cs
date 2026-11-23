// Runs Lubx.FixedStep headless with no input, and checks that 1 tick runs per frame and that no key / mouse edges are raised.
using System;
using Lub;
using Lubx;

public static class TestLubxFixedstepEdges
{
    static FixedStep? step;
    static int frame;
    static int ticks;

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            Width = 320,
            Height = 180,
        });
        step = new FixedStep(60, 1);
    }

    static void Tick(float tickDt)
    {
        ticks++;
        Check.That(!step!.KeyPressed("r"), "tick " + ticks + ": key_pressed(r) must stay false");
        Check.That(!step.KeyPressed("space"), "tick " + ticks + ": key_pressed(space) must stay false");
        Check.That(!step.MousePressed(1), "tick " + ticks + ": mouse_pressed(1) must stay false");
        Check.That(!step.KeyReleased("r"), "tick " + ticks + ": key_released(r) must stay false");
        // Names outside the first scan (function keys, aliases, SDL names, uppercase) can also be queried
        foreach (var key in new string[] { "f1", "f12", "return", "esc", "left shift", "keypad 1", "R" })
        {
            Check.That(!step.KeyPressed(key), "tick " + ticks + ": key_pressed(" + key + ") must stay false");
            Check.That(!step.KeyReleased(key), "tick " + ticks + ": key_released(" + key + ") must stay false");
        }
    }

    public static void OnFrame(float dt)
    {
        frame++;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        // dt = TickDt, so 1 tick runs per frame
        step!.Frame(step.TickDt, Tick);
        Check.That(ticks == frame, "frame " + frame + " must run exactly one tick (ran " + ticks + ")");
        if (frame == 3)
            Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
