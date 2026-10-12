// Lubx.FixedStep の key / mouse の edge が tick を消費したあとに消えることを見る。
// Lua 版は Input を差し替えて 1 frame だけ押されたことにしていたが、C# からは
// Input を差し替えられないので、入力が無い headless での tick 数と edge が立たないことだけを見る。
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
    }

    public static void OnFrame(float dt)
    {
        frame++;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        // dt = TickDt なので 1 frame に 1 tick 走る
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
