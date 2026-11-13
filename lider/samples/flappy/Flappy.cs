// lider を重ねて動かすための、いくつもの file に分けたゲーム。
// 規則 (重力、跳ぶ力、パイプの速さと隙間) は lub の samples/17_flappy に揃え、パイプを何本も流す。
// 手で跳ばない間は Pilot が代わりに跳ぶので、lider でコードを書いている間 (lider が入力を取る) も動き続ける。
// 手で跳ぶのは Space か左 click。
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
    // 鳥がこの高さを越えるか下回ると終わる
    const float Ceiling = 3.0f;

    // 終わってから次を始められるまでの秒数
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
