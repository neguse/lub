// lub の samples/09_breakout 相当を TinyC# で書いた entry。
// 変換と実行は samples/lub/run-lub.sh breakout を参照。
// gameplay は原典の rule (paddle/ball/bricks, key_down 駆動) を踏襲し、
// 型は Dynamic ではなく class Brick / out 引数の multi-return で表現する。

using System;
using System.Collections.Generic;

public class Brick
{
    public double x0;
    public double y0;
    public double x1;
    public double y1;
    public int row;
    public bool alive;
}

public static class Breakout
{
    const double DT = 1.0 / 60.0;
    const int STRIDE = 6; // pos.xy + color.rgba

    const int COLS = 11;
    const int ROWS = 6;
    const double BRICK_GAP_X = 0.018;
    const double BRICK_GAP_Y = 0.018;
    const double BRICK_LEFT = -0.88;
    const double BRICK_RIGHT = 0.88;
    const double BRICK_TOP = 0.76;
    const double BRICK_H = 0.06;
    const double BRICK_W =
        (BRICK_RIGHT - BRICK_LEFT - BRICK_GAP_X * (COLS - 1)) / COLS;

    const double PADDLE_Y = -0.78;
    const double PADDLE_W = 0.34;
    const double PADDLE_H = 0.045;
    const double PADDLE_SPEED = 1.55;

    const double BALL_R = 0.026;
    const double BALL_SPEED_X = 0.55;
    const double BALL_SPEED_Y = 0.83;

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
    static double ballVx = BALL_SPEED_X;
    static double ballVy = BALL_SPEED_Y;
    static bool ballStuck = true;
    static int lives = 3;
    static int score = 0;
    static double launchTimer = 0;
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

    static double Clamp(double v, double lo, double hi)
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
            double y1 = BRICK_TOP - (row - 1) * (BRICK_H + BRICK_GAP_Y);
            double y0 = y1 - BRICK_H;
            for (int col = 1; col <= COLS; col++)
            {
                double x0 = BRICK_LEFT + (col - 1) * (BRICK_W + BRICK_GAP_X);
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
        ballY = PADDLE_Y + PADDLE_H * 0.5 + BALL_R + 0.01;
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

    static bool CircleHitsRect(double cx, double cy, double r,
        double x0, double y0, double x1, double y1)
    {
        return cx + r > x0 && cx - r < x1 && cy + r > y0 && cy - r < y1;
    }

    static void BounceFromRect(Brick rect)
    {
        double left = ballX + BALL_R - rect.x0;
        double right = rect.x1 - (ballX - BALL_R);
        double bottom = ballY + BALL_R - rect.y0;
        double top = rect.y1 - (ballY - BALL_R);
        double m = Math.Min(Math.Min(left, right),
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
            -1 + PADDLE_W * 0.5 + 0.03, 1 - PADDLE_W * 0.5 - 0.03);

        if (ballStuck)
        {
            ballX = paddleX;
            ballY = PADDLE_Y + PADDLE_H * 0.5 + BALL_R + 0.01;
            launchTimer = launchTimer + DT;
            if (Input.key_down("space") || launchTimer > 1.0)
            {
                LaunchBall();
            }
            return;
        }

        ballX = ballX + ballVx * DT;
        ballY = ballY + ballVy * DT;

        if (ballX - BALL_R < -0.96)
        {
            ballX = -0.96 + BALL_R;
            ballVx = Math.Abs(ballVx);
        }
        else if (ballX + BALL_R > 0.96)
        {
            ballX = 0.96 - BALL_R;
            ballVx = -Math.Abs(ballVx);
        }
        if (ballY + BALL_R > 0.90)
        {
            ballY = 0.90 - BALL_R;
            ballVy = -Math.Abs(ballVy);
        }

        double px0 = paddleX - PADDLE_W * 0.5;
        double py0 = PADDLE_Y - PADDLE_H * 0.5;
        double px1 = paddleX + PADDLE_W * 0.5;
        double py1 = PADDLE_Y + PADDLE_H * 0.5;
        if (ballVy < 0 && CircleHitsRect(ballX, ballY, BALL_R, px0, py0, px1, py1))
        {
            double hit = (ballX - paddleX) / (PADDLE_W * 0.5);
            ballY = py1 + BALL_R;
            ballVx = Clamp(hit * 0.85 + (paddleX - paddlePrevX) * 2.5, -0.95, 0.95);
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

        if (ballY + BALL_R < -1.0)
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
            if (b.alive)
            {
                var c = rowColors[b.row - 1];
                AddRect(verts, b.x0, b.y0, b.x1, b.y1, c);
                AddRect(verts, b.x0 + 0.006, b.y1 - 0.012, b.x1 - 0.006,
                    b.y1 - 0.006, highlight);
            }
        }

        AddRect(verts, paddleX - PADDLE_W * 0.5, PADDLE_Y - PADDLE_H * 0.5,
            paddleX + PADDLE_W * 0.5, PADDLE_Y + PADDLE_H * 0.5, paddleColor);
        AddCircle(verts, ballX, ballY, BALL_R, ballColor);

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

    public static void onFrame(double dt)
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
            clear_color = new double[] { 0.035, 0.045, 0.065, 1.0 },
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
