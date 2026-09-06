// lub の samples/09_breakout 相当を TinyC# で書いた entry。
// 実行: lub samples/09_breakout/Breakout09.csproj (transpile + watch + hot reload)
// gameplay は原典の rule (paddle/ball/bricks, key_down 駆動) を踏襲し、
// 型は Dynamic ではなく class Brick / out 引数の multi-return で表現する。

using System;
using System.Collections.Generic;
using static Lub;

public class Brick
{
    public float X0;
    public float Y0;
    public float X1;
    public float Y1;
    public int Row;
    public bool Alive;
}

public static class Breakout09
{
    const float dt = 1.0f / 60.0f;
    const int stride = 6; // pos.xy + color.rgba

    const int cols = 11;
    const int rows = 6;
    const float brickGapX = 0.018f;
    const float brickGapY = 0.018f;
    const float brickLeft = -0.88f;
    const float brickRight = 0.88f;
    const float brickTop = 0.76f;
    const float brickH = 0.06f;
    const float brickW =
        (brickRight - brickLeft - brickGapX * (cols - 1)) / cols;

    const float paddleY = -0.78f;
    const float paddleW = 0.34f;
    const float paddleH = 0.045f;
    const float paddleSpeed = 1.55f;

    const float ballR = 0.026f;
    const float ballSpeedX = 0.55f;
    const float ballSpeedY = 0.83f;

    static List<float[]> rowColors = new List<float[]>
    {
        new float[] { 0.93f, 0.23f, 0.25f, 1.0f },
        new float[] { 0.96f, 0.62f, 0.16f, 1.0f },
        new float[] { 0.98f, 0.88f, 0.24f, 1.0f },
        new float[] { 0.22f, 0.72f, 0.43f, 1.0f },
        new float[] { 0.14f, 0.63f, 0.86f, 1.0f },
        new float[] { 0.55f, 0.42f, 0.86f, 1.0f },
    };

    static List<Brick> bricks = new List<Brick>();
    static float paddleX = 0;
    static float paddlePrevX = 0;
    static float ballX = 0;
    static float ballY = 0;
    static float ballVx = ballSpeedX;
    static float ballVy = ballSpeedY;
    static bool ballStuck = true;
    static int lives = 3;
    static int score = 0;
    static float launchTimer = 0;
    static FixedStep? step = null;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        Lub.Config(new ConfigOpts { Backend = backend });
        ResetGame();
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    static float Clamp(float v, float lo, float hi)
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
            float y1 = brickTop - (row - 1) * (brickH + brickGapY);
            float y0 = y1 - brickH;
            for (int col = 1; col <= cols; col++)
            {
                float x0 = brickLeft + (col - 1) * (brickW + brickGapX);
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
        ballY = paddleY + paddleH * 0.5f + ballR + 0.01f;
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

    static bool CircleHitsRect(float cx, float cy, float r,
        float x0, float y0, float x1, float y1)
    {
        return cx + r > x0 && cx - r < x1 && cy + r > y0 && cy - r < y1;
    }

    static void BounceFromRect(Brick rect)
    {
        float left = ballX + ballR - rect.X0;
        float right = rect.X1 - (ballX - ballR);
        float bottom = ballY + ballR - rect.Y0;
        float top = rect.Y1 - (ballY - ballR);
        float m = Math.Min(Math.Min(left, right),
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
            -1 + paddleW * 0.5f + 0.03f, 1 - paddleW * 0.5f - 0.03f);

        if (ballStuck)
        {
            ballX = paddleX;
            ballY = paddleY + paddleH * 0.5f + ballR + 0.01f;
            launchTimer = launchTimer + dt;
            if (Input.KeyDown("space") || launchTimer > 1.0f)
            {
                LaunchBall();
            }
            return;
        }

        ballX = ballX + ballVx * dt;
        ballY = ballY + ballVy * dt;

        if (ballX - ballR < -0.96f)
        {
            ballX = -0.96f + ballR;
            ballVx = Math.Abs(ballVx);
        }
        else if (ballX + ballR > 0.96f)
        {
            ballX = 0.96f - ballR;
            ballVx = -Math.Abs(ballVx);
        }
        if (ballY + ballR > 0.90f)
        {
            ballY = 0.90f - ballR;
            ballVy = -Math.Abs(ballVy);
        }

        float px0 = paddleX - paddleW * 0.5f;
        float py0 = paddleY - paddleH * 0.5f;
        float px1 = paddleX + paddleW * 0.5f;
        float py1 = paddleY + paddleH * 0.5f;
        if (ballVy < 0 && CircleHitsRect(ballX, ballY, ballR, px0, py0, px1, py1))
        {
            float hit = (ballX - paddleX) / (paddleW * 0.5f);
            ballY = py1 + ballR;
            ballVx = Clamp(hit * 0.85f + (paddleX - paddlePrevX) * 2.5f, -0.95f, 0.95f);
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

        if (ballY + ballR < -1.0f)
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

    static void PushVertex(List<float> verts, float x, float y, float[] c)
    {
        verts.Add(x);
        verts.Add(y);
        verts.Add(c[0]);
        verts.Add(c[1]);
        verts.Add(c[2]);
        verts.Add(c[3]);
    }

    static void AddRect(List<float> verts, float x0, float y0,
        float x1, float y1, float[] c)
    {
        PushVertex(verts, x0, y0, c);
        PushVertex(verts, x1, y0, c);
        PushVertex(verts, x1, y1, c);
        PushVertex(verts, x0, y0, c);
        PushVertex(verts, x1, y1, c);
        PushVertex(verts, x0, y1, c);
    }

    static void AddCircle(List<float> verts, float cx, float cy, float r,
        float[] c)
    {
        int segments = 20;
        for (int i = 0; i < segments; i++)
        {
            float a0 = (float)i / segments * (float)Math.PI * 2;
            float a1 = (float)(i + 1) / segments * (float)Math.PI * 2;
            PushVertex(verts, cx, cy, c);
            PushVertex(verts, cx + (float)Math.Cos(a0) * r,
                cy + (float)Math.Sin(a0) * r, c);
            PushVertex(verts, cx + (float)Math.Cos(a1) * r,
                cy + (float)Math.Sin(a1) * r, c);
        }
    }

    static List<float> BuildVertices()
    {
        var verts = new List<float>();
        var rail = new float[] { 0.18f, 0.22f, 0.30f, 1.0f };
        var paddleColor = new float[] { 0.95f, 0.96f, 0.88f, 1.0f };
        var ballColor = new float[] { 1.0f, 0.98f, 0.78f, 1.0f };
        var liveColor = new float[] { 0.92f, 0.34f, 0.36f, 1.0f };
        var scoreColor = new float[] { 0.30f, 0.82f, 0.65f, 1.0f };
        var highlight = new float[] { 1.0f, 1.0f, 1.0f, 0.20f };

        AddRect(verts, -0.99f, -0.98f, -0.96f, 0.93f, rail);
        AddRect(verts, 0.96f, -0.98f, 0.99f, 0.93f, rail);
        AddRect(verts, -0.99f, 0.90f, 0.99f, 0.93f, rail);

        foreach (var b in bricks)
        {
            if (b.Alive)
            {
                var c = rowColors[b.Row - 1];
                AddRect(verts, b.X0, b.Y0, b.X1, b.Y1, c);
                AddRect(verts, b.X0 + 0.006f, b.Y1 - 0.012f, b.X1 - 0.006f,
                    b.Y1 - 0.006f, highlight);
            }
        }

        AddRect(verts, paddleX - paddleW * 0.5f, paddleY - paddleH * 0.5f,
            paddleX + paddleW * 0.5f, paddleY + paddleH * 0.5f, paddleColor);
        AddCircle(verts, ballX, ballY, ballR, ballColor);

        for (int i = 1; i <= lives; i++)
        {
            AddCircle(verts, -0.86f + (i - 1) * 0.07f, -0.92f, 0.018f, liveColor);
        }
        int scoreShow = score < 12 ? score : 12;
        for (int i = 1; i <= scoreShow; i++)
        {
            float x = 0.48f + (i - 1) * 0.035f;
            AddRect(verts, x, -0.94f, x + 0.018f, -0.90f, scoreColor);
        }

        return verts;
    }

    public static void OnFrame(float dt)
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
            ClearColor = new float[] { 0.035f, 0.045f, 0.065f, 1.0f },
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
