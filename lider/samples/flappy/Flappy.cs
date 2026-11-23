// A game split into several files, for running with lider layered on top.
// The rules (gravity, jump strength, pipe speed and gap) match lub's samples/17_flappy, and many pipes scroll by.
// While the player does not jump by hand, Pilot jumps instead, so the game keeps running while you write code in lider (lider takes the input).
// Jump by hand with Space or left click.
using System;
using Lub;

public enum Phase
{
    Ready,
    Play,
    Over,
}

public static class Flappy
{
    // The game ends when the bird goes above or below this height
    const float Ceiling = 3.0f;

    // Seconds after the end before the next round can start
    const float OverWait = 1.0f;

    public static Phase State;
    public static Bird Bird = new Bird();
    public static PipeField Field = new PipeField();
    public static Pilot Pilot = new Pilot();
    public static int Score;
    public static int Best;
    static float overFor;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 1280, Height = 720 });
    }

    public static void OnFrame(float dt)
    {
        var manual = Input.KeyPressed("space") || Input.MousePressed();
        Step(dt, Pilot.Flap(manual, dt, Bird, Field.Next()));
        Render.Frame();
    }

    static void Step(float dt, bool flap)
    {
        switch (State)
        {
            case Phase.Ready:
                Bird.Hover(dt);
                if (flap || Pilot.InCharge())
                {
                    State = Phase.Play;
                    Bird.Flap();
                }
                break;
            case Phase.Play:
                if (flap)
                    Bird.Flap();
                Bird.Step(dt);
                Score += Field.Step(dt);
                if (Bird.Y > Ceiling || Bird.Y < -Ceiling || Field.Hits(Bird))
                {
                    State = Phase.Over;
                    Best = Math.Max(Best, Score);
                    overFor = 0;
                }
                break;
            case Phase.Over:
                overFor += dt;
                if ((flap || Pilot.InCharge()) && overFor >= OverWait)
                    Restart();
                break;
        }
    }

    static void Restart()
    {
        State = Phase.Ready;
        Score = 0;
        Bird.Reset();
        Field.Reset();
    }

    public static void Draw(Mat4 vp)
    {
        var ground =
            Mat4.Translate(new Vec3(0, -Ceiling - 2.5f, 0))
            * Mat4.Scale(new Vec3(14, 5, 1.5f));
        Gfx.Draw(36, Render.Cube(vp * ground, Render.GroundColor), Render.Opts);
        Field.Draw(vp);
        Bird.Draw(vp, State == Phase.Over);
    }
}
