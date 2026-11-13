// 右から流れてくるパイプ。上下の 2 本の間に、鳥が通れる隙間がある。
using System;
using System.Collections.Generic;
using Lub;

public class Pipe
{
    public const float Width = 0.8f;
    public const float Gap = 2.0f;
    const float Length = 5.0f;

    public float X;
    public float GapY;
    public bool Passed;

    public Pipe(float x, float gapY)
    {
        X = x;
        GapY = gapY;
    }

    public bool Hits(Bird bird)
    {
        if (Math.Abs(X - Bird.X) > (Width + Bird.Size) * 0.5f)
            return false;
        var room = (Gap - Bird.Size) * 0.5f;
        return bird.Y > GapY + room || bird.Y < GapY - room;
    }

    public void Draw(Mat4 vp)
    {
        var scale = Mat4.Scale(new Vec3(Width, Length, Width));
        var top = Mat4.Translate(new Vec3(X, GapY + (Gap + Length) * 0.5f, 0)) * scale;
        Gfx.Draw(36, Render.Cube(vp * top, Render.PipeColor), Render.Opts);
        var bottom =
            Mat4.Translate(new Vec3(X, GapY - (Gap + Length) * 0.5f, 0)) * scale;
        Gfx.Draw(36, Render.Cube(vp * bottom, Render.PipeColor), Render.Opts);
    }
}

public class PipeField
{
    const float Speed = 2.0f;
    const float Spacing = 4.0f;
    const float SpawnX = 7.0f;
    const float EndX = -7.0f;

    public List<Pipe> Pipes = new List<Pipe>();
    float t;

    public void Reset()
    {
        Pipes.Clear();
    }

    // 流して、鳥を越えたパイプの数を返す
    public int Step(float dt)
    {
        t += dt;
        foreach (var pipe in Pipes)
            pipe.X -= Speed * dt;
        Pipes.RemoveAll(p => p.X < EndX);
        if (Pipes.Count == 0 || Pipes[Pipes.Count - 1].X < SpawnX - Spacing)
            Pipes.Add(new Pipe(SpawnX, (float)Math.Sin(t * 1.7f) * 1.5f));
        var passed = 0;
        foreach (var pipe in Pipes)
        {
            if (!pipe.Passed && pipe.X < Bird.X)
            {
                pipe.Passed = true;
                passed++;
            }
        }
        return passed;
    }

    // 鳥がまだ抜けきっていない一番手前のパイプ
    public Pipe? Next()
    {
        foreach (var pipe in Pipes)
        {
            if (pipe.X + Pipe.Width * 0.5f > Bird.X - Bird.Size * 0.5f)
                return pipe;
        }
        return null;
    }

    public bool Hits(Bird bird)
    {
        foreach (var pipe in Pipes)
        {
            if (pipe.Hits(bird))
                return true;
        }
        return false;
    }

    public void Draw(Mat4 vp)
    {
        foreach (var pipe in Pipes)
            pipe.Draw(vp);
    }
}
