// 鳥。重力で落ち、跳ぶと上向きの速さを得る。
using System;
using Lub;

public class Bird
{
    public const float X = -2.0f;
    public const float Size = 0.4f;
    const float Gravity = 8.0f;
    const float FlapSpeed = 3.0f;

    public float Y;
    public float Vy;
    public int Flaps;
    float spin;

    public void Reset()
    {
        Y = 0;
        Vy = 0;
        Flaps = 0;
    }

    // 始める前は、その場で小さく上下する
    public void Hover(float dt)
    {
        spin += dt;
        Y = (float)Math.Sin(spin * 4) * 0.15f;
    }

    public void Flap()
    {
        Vy = FlapSpeed;
        Flaps++;
    }

    public void Step(float dt)
    {
        Vy -= Gravity * dt;
        Y += Vy * dt;
        spin += dt * 3;
    }

    public void Draw(Mat4 vp, bool dead)
    {
        var model =
            Mat4.Translate(new Vec3(X, Y, 0))
            * Mat4.RotateY(-spin)
            * Mat4.Scale(new Vec3(Size, Size, Size));
        Gfx.Draw(
            36,
            Render.Cube(vp * model, dead ? Render.DeadColor : Render.BirdColor),
            Render.Opts
        );
    }
}
