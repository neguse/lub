// lub の samples/09_breakout 相当を TinyC# で書いた entry。
// 実行: lub samples/09_breakout/Breakout09.csproj (transpile + watch + hot reload)
// gameplay は原典の rule (paddle/ball/bricks, key_down 駆動) を踏襲し、
// 型は Dynamic ではなく class Brick / out 引数の multi-return で表現する。

using System;
using System.Collections.Generic;
using static Lub;

public class Brick
{
    public double X0;
    public double Y0;
    public double X1;
    public double Y1;
    public int Row;
    public bool Alive;
}

public static class Breakout09
{
    const double dt = 1.0 / 60.0;
    const int stride = 6; // pos.xy + color.rgba

    const int cols = 11;
    const int rows = 6;
    const double brickGapX = 0.018;
    const double brickGapY = 0.018;
    const double brickLeft = -0.88;
    const double brickRight = 0.88;
    const double brickTop = 0.76;
    const double brickH = 0.06;
    const double brickW =
        (brickRight - brickLeft - brickGapX * (cols - 1)) / cols;

    const double paddleY = -0.78;
    const double paddleW = 0.34;
    const double paddleH = 0.045;
    const double paddleSpeed = 1.55;

    const double ballR = 0.026;
    const double ballSpeedX = 0.55;
    const double ballSpeedY = 0.83;

    static List<double[]> rowColors = new List<double[]>
    {
        new double[] { 0.93, 0.23, 0.25, 1.0 },
        new double[] { 0.96, 0.62, 0.16, 1.0 },
        new double[] { 0.98, 0.88, 0.24, 1.0 },
        new double[] { 0.22, 0.72, 0.43, 1.0 },
        new double[] { 0.14, 0.63, 0.86, 1.0 },
        new double[] { 0.55, 0.42, 0.86, 1.0 },
    };

    static List<Brick> bricks = new List<Brick>();
    static double paddleX = 0;
    static double paddlePrevX = 0;
    static double ballX = 0;
    static double ballY = 0;
    static double ballVx = ballSpeedX;
    static double ballVy = ballSpeedY;
    static bool ballStuck = true;
    static int lives = 3;
    static int score = 0;
    static double launchTimer = 0;
    static FixedStep? step = null;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "native";
        Lub.Config(new ConfigOpts { Backend = backend });
        ResetGame();
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    static double Clamp(double v, double lo, double hi)
    {
        if (v < lo) return lo;
        if (v > hi) return hi;
        return v;
    }

    static void ResetBricks()
    {
        bricks = new List<Brick>();
        for (int row = 1; row <= rows; row++)
        {
            double y1 = brickTop - (row - 1) * (brickH + brickGapY);
            double y0 = y1 - brickH;
            for (int col = 1; col <= cols; col++)
            {
                double x0 = brickLeft + (col - 1) * (brickW + brickGapX);
                bricks.Add(new Brick
                {
                    X0 = x0,
                    Y0 = y0,
                    X1 = x0 + brickW,
                    Y1 = y1,
                    Row = row,
                    Alive = true,
                });
            }
        }
    }

    static void ResetBall()
    {
        ballX = paddleX;
        ballY = paddleY + paddleH * 0.5 + ballR + 0.01;
        ballVx = ballSpeedX;
        ballVy = ballSpeedY;
        ballStuck = true;
        launchTimer = 0;
    }

    static void ResetGame()
    {
        paddleX = 0;
        paddlePrevX = 0;
        lives = 3;
        score = 0;
        ResetBricks();
        ResetBall();
    }

    static void LaunchBall()
    {
        if (!ballStuck) return;
        ballStuck = false;
        ballVx = paddleX >= 0 ? -ballSpeedX : ballSpeedX;
        ballVy = ballSpeedY;
    }

    static int AliveBricks()
    {
        int n = 0;
        foreach (var b in bricks)
        {
            if (b.Alive) n = n + 1;
        }
        return n;
    }

    static bool CircleHitsRect(double cx, double cy, double r,
        double x0, double y0, double x1, double y1)
    {
        return cx + r > x0 && cx - r < x1 && cy + r > y0 && cy - r < y1;
    }

    static void BounceFromRect(Brick rect)
    {
        double left = ballX + ballR - rect.X0;
        double right = rect.X1 - (ballX - ballR);
        double bottom = ballY + ballR - rect.Y0;
        double top = rect.Y1 - (ballY - ballR);
        double m = Math.Min(Math.Min(left, right),
            Math.Min(bottom, top));

        if (m == left)
        {
            ballX = rect.X0 - ballR;
            ballVx = -Math.Abs(ballVx);
        }
        else if (m == right)
        {
            ballX = rect.X1 + ballR;
            ballVx = Math.Abs(ballVx);
        }
        else if (m == bottom)
        {
            ballY = rect.Y0 - ballR;
            ballVy = -Math.Abs(ballVy);
        }
        else
        {
            ballY = rect.Y1 + ballR;
            ballVy = Math.Abs(ballVy);
        }
    }

    static void UpdateGame(bool resetPressed)
    {
        if (resetPressed)
        {
            ResetGame();
        }

        int move = 0;
        if (Input.KeyDown("left") || Input.KeyDown("a")) move = move - 1;
        if (Input.KeyDown("right") || Input.KeyDown("d")) move = move + 1;

        paddlePrevX = paddleX;
        paddleX = Clamp(paddleX + move * paddleSpeed * dt,
            -1 + paddleW * 0.5 + 0.03, 1 - paddleW * 0.5 - 0.03);

        if (ballStuck)
        {
            ballX = paddleX;
            ballY = paddleY + paddleH * 0.5 + ballR + 0.01;
            launchTimer = launchTimer + dt;
            if (Input.KeyDown("space") || launchTimer > 1.0)
            {
                LaunchBall();
            }
            return;
        }

        ballX = ballX + ballVx * dt;
        ballY = ballY + ballVy * dt;

        if (ballX - ballR < -0.96)
        {
            ballX = -0.96 + ballR;
            ballVx = Math.Abs(ballVx);
        }
        else if (ballX + ballR > 0.96)
        {
            ballX = 0.96 - ballR;
            ballVx = -Math.Abs(ballVx);
        }
        if (ballY + ballR > 0.90)
        {
            ballY = 0.90 - ballR;
            ballVy = -Math.Abs(ballVy);
        }

        double px0 = paddleX - paddleW * 0.5;
        double py0 = paddleY - paddleH * 0.5;
        double px1 = paddleX + paddleW * 0.5;
        double py1 = paddleY + paddleH * 0.5;
        if (ballVy < 0 && CircleHitsRect(ballX, ballY, ballR, px0, py0, px1, py1))
        {
            double hit = (ballX - paddleX) / (paddleW * 0.5);
            ballY = py1 + ballR;
            ballVx = Clamp(hit * 0.85 + (paddleX - paddlePrevX) * 2.5, -0.95, 0.95);
            ballVy = Math.Abs(ballVy);
        }

        foreach (var b in bricks)
        {
            if (b.Alive && CircleHitsRect(ballX, ballY, ballR, b.X0, b.Y0, b.X1, b.Y1))
            {
                b.Alive = false;
                score = score + 1;
                BounceFromRect(b);
                break;
            }
        }

        if (ballY + ballR < -1.0)
        {
            lives = lives - 1;
            if (lives <= 0)
            {
                ResetGame();
            }
            else
            {
                ResetBall();
            }
        }
        else if (AliveBricks() == 0)
        {
            ResetGame();
        }
    }

    static void PushVertex(List<double> verts, double x, double y, double[] c)
    {
        verts.Add(x);
        verts.Add(y);
        verts.Add(c[0]);
        verts.Add(c[1]);
        verts.Add(c[2]);
        verts.Add(c[3]);
    }

    static void AddRect(List<double> verts, double x0, double y0,
        double x1, double y1, double[] c)
    {
        PushVertex(verts, x0, y0, c);
        PushVertex(verts, x1, y0, c);
        PushVertex(verts, x1, y1, c);
        PushVertex(verts, x0, y0, c);
        PushVertex(verts, x1, y1, c);
        PushVertex(verts, x0, y1, c);
    }

    static void AddCircle(List<double> verts, double cx, double cy, double r,
        double[] c)
    {
        int segments = 20;
        for (int i = 0; i < segments; i++)
        {
            double a0 = (double)i / segments * Math.PI * 2;
            double a1 = (double)(i + 1) / segments * Math.PI * 2;
            PushVertex(verts, cx, cy, c);
            PushVertex(verts, cx + Math.Cos(a0) * r,
                cy + Math.Sin(a0) * r, c);
            PushVertex(verts, cx + Math.Cos(a1) * r,
                cy + Math.Sin(a1) * r, c);
        }
    }

    static List<double> BuildVertices()
    {
        var verts = new List<double>();
        var rail = new double[] { 0.18, 0.22, 0.30, 1.0 };
        var paddleColor = new double[] { 0.95, 0.96, 0.88, 1.0 };
        var ballColor = new double[] { 1.0, 0.98, 0.78, 1.0 };
        var liveColor = new double[] { 0.92, 0.34, 0.36, 1.0 };
        var scoreColor = new double[] { 0.30, 0.82, 0.65, 1.0 };
        var highlight = new double[] { 1.0, 1.0, 1.0, 0.20 };

        AddRect(verts, -0.99, -0.98, -0.96, 0.93, rail);
        AddRect(verts, 0.96, -0.98, 0.99, 0.93, rail);
        AddRect(verts, -0.99, 0.90, 0.99, 0.93, rail);

        foreach (var b in bricks)
        {
            if (b.Alive)
            {
                var c = rowColors[b.Row - 1];
                AddRect(verts, b.X0, b.Y0, b.X1, b.Y1, c);
                AddRect(verts, b.X0 + 0.006, b.Y1 - 0.012, b.X1 - 0.006,
                    b.Y1 - 0.006, highlight);
            }
        }

        AddRect(verts, paddleX - paddleW * 0.5, paddleY - paddleH * 0.5,
            paddleX + paddleW * 0.5, paddleY + paddleH * 0.5, paddleColor);
        AddCircle(verts, ballX, ballY, ballR, ballColor);

        for (int i = 1; i <= lives; i++)
        {
            AddCircle(verts, -0.86 + (i - 1) * 0.07, -0.92, 0.018, liveColor);
        }
        int scoreShow = score < 12 ? score : 12;
        for (int i = 1; i <= scoreShow; i++)
        {
            double x = 0.48 + (i - 1) * 0.035;
            AddRect(verts, x, -0.94, x + 0.018, -0.90, scoreColor);
        }

        return verts;
    }

    public static void OnFrame(double dt)
    {
        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.Frame(dt, _ => UpdateGame(stepNow.KeyPressed("r")));

        Io.LoadText("samples/09_breakout/data/09_breakout.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.LoadText("samples/09_breakout/data/09_breakout.fs.slang",
            out var fs, out var fsv, out _, out _);
        if (vs == null || fs == null) return;

        var verts = BuildVertices();
        var shader = Gfx.UseShader("breakout_shader", vs, fs, vsv * 31 + fsv);
        var vbuf = Gfx.UseBuffer("breakout_verts", Gfx.BufferType.Vertex, verts);
        if (shader == null || vbuf == null) return;

        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new double[] { 0.035, 0.045, 0.065, 1.0 },
        });
        Gfx.Draw(verts.Count / stride,
            new Dictionary<string, object> { ["verts"] = vbuf },
            new DrawOpts
            {
                Shader = shader,
                Depth = false,
                Cull = Gfx.Cull.None,
                Blend = Gfx.Blend.Alpha,
            });
        Gfx.EndPass();
    }
}
