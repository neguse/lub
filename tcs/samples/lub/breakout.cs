// lub の samples/09_breakout 相当を TinyC# で書いた entry。
// 変換と実行は samples/lub/run-lub.sh breakout を参照。
// gameplay は原典の rule (paddle/ball/bricks, key_down 駆動) を踏襲し、
// 型は Dynamic ではなく class Brick / out 引数の multi-return で表現する。

using System;
using System.Collections.Generic;

public class Brick
{
    public float x0;
    public float y0;
    public float x1;
    public float y1;
    public int row;
    public bool alive;
}

public static class Breakout
{
    const float DT = 1.0f / 60.0f;
    const int STRIDE = 6; // pos.xy + color.rgba

    const int COLS = 11;
    const int ROWS = 6;
    const float BRICK_GAP_X = 0.018f;
    const float BRICK_GAP_Y = 0.018f;
    const float BRICK_LEFT = -0.88f;
    const float BRICK_RIGHT = 0.88f;
    const float BRICK_TOP = 0.76f;
    const float BRICK_H = 0.06f;
    const float BRICK_W =
        (BRICK_RIGHT - BRICK_LEFT - BRICK_GAP_X * (COLS - 1)) / COLS;

    const float PADDLE_Y = -0.78f;
    const float PADDLE_W = 0.34f;
    const float PADDLE_H = 0.045f;
    const float PADDLE_SPEED = 1.55f;

    const float BALL_R = 0.026f;
    const float BALL_SPEED_X = 0.55f;
    const float BALL_SPEED_Y = 0.83f;

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
    static float ballVx = BALL_SPEED_X;
    static float ballVy = BALL_SPEED_Y;
    static bool ballStuck = true;
    static int lives = 3;
    static int score = 0;
    static float launchTimer = 0;
    static int meshVersion = 0;

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND") ?? "native";
        Lub.config(new ConfigOpts { backend = backend });
        ResetGame();
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
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
        for (int row = 1; row <= ROWS; row++)
        {
            float y1 = BRICK_TOP - (row - 1) * (BRICK_H + BRICK_GAP_Y);
            float y0 = y1 - BRICK_H;
            for (int col = 1; col <= COLS; col++)
            {
                float x0 = BRICK_LEFT + (col - 1) * (BRICK_W + BRICK_GAP_X);
                bricks.Add(new Brick
                {
                    x0 = x0,
                    y0 = y0,
                    x1 = x0 + BRICK_W,
                    y1 = y1,
                    row = row,
                    alive = true,
                });
            }
        }
    }

    static void ResetBall()
    {
        ballX = paddleX;
        ballY = PADDLE_Y + PADDLE_H * 0.5f + BALL_R + 0.01f;
        ballVx = BALL_SPEED_X;
        ballVy = BALL_SPEED_Y;
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
        ballVx = paddleX >= 0 ? -BALL_SPEED_X : BALL_SPEED_X;
        ballVy = BALL_SPEED_Y;
    }

    static int AliveBricks()
    {
        int n = 0;
        foreach (var b in bricks)
        {
            if (b.alive) n = n + 1;
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
        float left = ballX + BALL_R - rect.x0;
        float right = rect.x1 - (ballX - BALL_R);
        float bottom = ballY + BALL_R - rect.y0;
        float top = rect.y1 - (ballY - BALL_R);
        float m = Math.Min(Math.Min(left, right),
            Math.Min(bottom, top));

        if (m == left)
        {
            ballX = rect.x0 - BALL_R;
            ballVx = -Math.Abs(ballVx);
        }
        else if (m == right)
        {
            ballX = rect.x1 + BALL_R;
            ballVx = Math.Abs(ballVx);
        }
        else if (m == bottom)
        {
            ballY = rect.y0 - BALL_R;
            ballVy = -Math.Abs(ballVy);
        }
        else
        {
            ballY = rect.y1 + BALL_R;
            ballVy = Math.Abs(ballVy);
        }
    }

    static void UpdateGame()
    {
        if (Input.key_pressed("r"))
        {
            ResetGame();
        }

        int move = 0;
        if (Input.key_down("left") || Input.key_down("a")) move = move - 1;
        if (Input.key_down("right") || Input.key_down("d")) move = move + 1;

        paddlePrevX = paddleX;
        paddleX = Clamp(paddleX + move * PADDLE_SPEED * DT,
            -1 + PADDLE_W * 0.5f + 0.03f, 1 - PADDLE_W * 0.5f - 0.03f);

        if (ballStuck)
        {
            ballX = paddleX;
            ballY = PADDLE_Y + PADDLE_H * 0.5f + BALL_R + 0.01f;
            launchTimer = launchTimer + DT;
            if (Input.key_down("space") || launchTimer > 1.0f)
            {
                LaunchBall();
            }
            return;
        }

        ballX = ballX + ballVx * DT;
        ballY = ballY + ballVy * DT;

        if (ballX - BALL_R < -0.96f)
        {
            ballX = -0.96f + BALL_R;
            ballVx = Math.Abs(ballVx);
        }
        else if (ballX + BALL_R > 0.96f)
        {
            ballX = 0.96f - BALL_R;
            ballVx = -Math.Abs(ballVx);
        }
        if (ballY + BALL_R > 0.90f)
        {
            ballY = 0.90f - BALL_R;
            ballVy = -Math.Abs(ballVy);
        }

        float px0 = paddleX - PADDLE_W * 0.5f;
        float py0 = PADDLE_Y - PADDLE_H * 0.5f;
        float px1 = paddleX + PADDLE_W * 0.5f;
        float py1 = PADDLE_Y + PADDLE_H * 0.5f;
        if (ballVy < 0 && CircleHitsRect(ballX, ballY, BALL_R, px0, py0, px1, py1))
        {
            float hit = (ballX - paddleX) / (PADDLE_W * 0.5f);
            ballY = py1 + BALL_R;
            ballVx = Clamp(hit * 0.85f + (paddleX - paddlePrevX) * 2.5f, -0.95f, 0.95f);
            ballVy = Math.Abs(ballVy);
        }

        foreach (var b in bricks)
        {
            if (b.alive && CircleHitsRect(ballX, ballY, BALL_R, b.x0, b.y0, b.x1, b.y1))
            {
                b.alive = false;
                score = score + 1;
                BounceFromRect(b);
                break;
            }
        }

        if (ballY + BALL_R < -1.0f)
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
            if (b.alive)
            {
                var c = rowColors[b.row - 1];
                AddRect(verts, b.x0, b.y0, b.x1, b.y1, c);
                AddRect(verts, b.x0 + 0.006f, b.y1 - 0.012f, b.x1 - 0.006f,
                    b.y1 - 0.006f, highlight);
            }
        }

        AddRect(verts, paddleX - PADDLE_W * 0.5f, PADDLE_Y - PADDLE_H * 0.5f,
            paddleX + PADDLE_W * 0.5f, PADDLE_Y + PADDLE_H * 0.5f, paddleColor);
        AddCircle(verts, ballX, ballY, BALL_R, ballColor);

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

    public static void onFrame(float dt)
    {
        UpdateGame();

        Io.load_text("samples/09_breakout/data/09_breakout.vs.slang",
            out var vs, out var vsv, out _);
        Io.load_text("samples/09_breakout/data/09_breakout.fs.slang",
            out var fs, out var fsv, out _);
        if (vs == null || fs == null) return;

        var verts = BuildVertices();
        meshVersion = meshVersion + 1;
        var shader = Gfx.use_shader("breakout_shader", vs, fs, vsv * 31 + fsv);
        var vbuf = Gfx.use_buffer("breakout_verts", Gfx.VERTEX, verts,
            meshVersion);

        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new float[] { 0.035f, 0.045f, 0.065f, 1.0f },
        });
        Gfx.draw(verts.Count / STRIDE, new DrawBindings { verts = vbuf },
            new DrawOpts
            {
                shader = shader,
                depth = false,
                cull = Gfx.NONE,
                blend = Gfx.ALPHA,
            });
        Gfx.end_pass();
    }
}
