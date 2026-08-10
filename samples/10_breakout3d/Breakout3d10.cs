// lub の samples/10_breakout3d 相当を TinyC# で書いた entry。
// 実行: lub samples/10_breakout3d/Breakout3d10.csproj (transpile + watch + hot reload)
// gameplay は原典の rule (paddle/ball/bricks, key_down 駆動) を踏襲し、
// 型は Dynamic ではなく class Brick / out 引数の multi-return で表現する。
// MVP とカメラ揺れはフレーム決定的な cameraT から導出する。

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

public static class Breakout3d10
{
    const float DT = 1.0f / 60.0f;
    const int STRIDE = 7; // pos.xyz + color.rgba

    const int COLS = 9;
    const int ROWS = 5;
    const float BRICK_GAP_X = 0.035f;
    const float BRICK_GAP_Y = 0.03f;
    const float BRICK_LEFT = -0.83f;
    const float BRICK_RIGHT = 0.83f;
    const float BRICK_TOP = 0.70f;
    const float BRICK_H = 0.075f;
    const float BRICK_D = 0.16f;
    const float BRICK_W =
        (BRICK_RIGHT - BRICK_LEFT - BRICK_GAP_X * (COLS - 1)) / COLS;

    const float PADDLE_Y = -0.76f;
    const float PADDLE_W = 0.38f;
    const float PADDLE_H = 0.055f;
    const float PADDLE_D = 0.24f;
    const float PADDLE_SPEED = 1.55f;

    const float BALL_R = 0.035f;
    const float BALL_SPEED_X = 0.58f;
    const float BALL_SPEED_Y = 0.85f;

    static List<float[]> rowColors = new List<float[]>
    {
        new float[] { 0.95f, 0.24f, 0.28f, 1.0f },
        new float[] { 0.98f, 0.55f, 0.15f, 1.0f },
        new float[] { 0.98f, 0.86f, 0.22f, 1.0f },
        new float[] { 0.22f, 0.70f, 0.40f, 1.0f },
        new float[] { 0.16f, 0.58f, 0.88f, 1.0f },
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
    static FixedStep? step = null;
    static float cameraT = 0;

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND");
        Lub.config(new ConfigOpts { backend = backend });
        ResetGame();
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    static float[] Shade(float[] c, float k)
    {
        return new float[]
        {
            MathUtil.clamp(c[0] * k, 0, 1),
            MathUtil.clamp(c[1] * k, 0, 1),
            MathUtil.clamp(c[2] * k, 0, 1),
            c[3],
        };
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
        ballY = PADDLE_Y + PADDLE_H * 0.5f + BALL_R + 0.015f;
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

    static void UpdateGame(bool resetPressed)
    {
        if (resetPressed)
        {
            ResetGame();
        }

        int move = 0;
        if (Input.key_down("left") || Input.key_down("a")) move = move - 1;
        if (Input.key_down("right") || Input.key_down("d")) move = move + 1;

        paddlePrevX = paddleX;
        paddleX = MathUtil.clamp(paddleX + move * PADDLE_SPEED * DT,
            -1 + PADDLE_W * 0.5f + 0.05f, 1f - PADDLE_W * 0.5f - 0.05f);

        if (ballStuck)
        {
            ballX = paddleX;
            ballY = PADDLE_Y + PADDLE_H * 0.5f + BALL_R + 0.015f;
            launchTimer = launchTimer + DT;
            if (Input.key_down("space") || launchTimer > 1.0f)
            {
                LaunchBall();
            }
            return;
        }

        ballX = ballX + ballVx * DT;
        ballY = ballY + ballVy * DT;

        if (ballX - BALL_R < -0.95f)
        {
            ballX = -0.95f + BALL_R;
            ballVx = Math.Abs(ballVx);
        }
        else if (ballX + BALL_R > 0.95f)
        {
            ballX = 0.95f - BALL_R;
            ballVx = -Math.Abs(ballVx);
        }
        if (ballY + BALL_R > 0.88f)
        {
            ballY = 0.88f - BALL_R;
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
            ballVx = MathUtil.clamp(hit * 0.9f + (paddleX - paddlePrevX) * 2.5f,
                -0.98f, 0.98f);
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

    static void PushVertex(List<float> verts, float x, float y, float z,
        float[] c)
    {
        verts.Add(x);
        verts.Add(y);
        verts.Add(z);
        verts.Add(c[0]);
        verts.Add(c[1]);
        verts.Add(c[2]);
        verts.Add(c[3]);
    }

    static void Quad(List<float> verts, float[] a, float[] b, float[] c,
        float[] d, float[] col)
    {
        PushVertex(verts, a[0], a[1], a[2], col);
        PushVertex(verts, b[0], b[1], b[2], col);
        PushVertex(verts, c[0], c[1], c[2], col);
        PushVertex(verts, a[0], a[1], a[2], col);
        PushVertex(verts, c[0], c[1], c[2], col);
        PushVertex(verts, d[0], d[1], d[2], col);
    }

    static void AddBox(List<float> verts, float cx, float cy, float cz,
        float sx, float sy, float sz, float[] baseColor)
    {
        float x0 = cx - sx * 0.5f;
        float x1 = cx + sx * 0.5f;
        float y0 = cy - sy * 0.5f;
        float y1 = cy + sy * 0.5f;
        float z0 = cz - sz * 0.5f;
        float z1 = cz + sz * 0.5f;

        var p000 = new float[] { x0, y0, z0 };
        var p100 = new float[] { x1, y0, z0 };
        var p010 = new float[] { x0, y1, z0 };
        var p110 = new float[] { x1, y1, z0 };
        var p001 = new float[] { x0, y0, z1 };
        var p101 = new float[] { x1, y0, z1 };
        var p011 = new float[] { x0, y1, z1 };
        var p111 = new float[] { x1, y1, z1 };

        Quad(verts, p000, p100, p110, p010, Shade(baseColor, 1.05f));
        Quad(verts, p101, p001, p011, p111, Shade(baseColor, 0.58f));
        Quad(verts, p001, p000, p010, p011, Shade(baseColor, 0.72f));
        Quad(verts, p100, p101, p111, p110, Shade(baseColor, 0.82f));
        Quad(verts, p010, p110, p111, p011, Shade(baseColor, 1.22f));
        Quad(verts, p001, p101, p100, p000, Shade(baseColor, 0.48f));
    }

    static float[] SpherePoint(float cx, float cy, float cz, float r,
        float u, float vv)
    {
        float cv = (float)Math.Cos(vv);
        return new float[]
        {
            cx + (float)Math.Cos(u) * cv * r,
            cy + (float)Math.Sin(vv) * r,
            cz + (float)Math.Sin(u) * cv * r,
            (float)Math.Cos(u) * cv,
            (float)Math.Sin(vv),
            (float)Math.Sin(u) * cv,
        };
    }

    static float[] SphereCol(float[] baseColor, float[] pt)
    {
        float ny = pt[4] > 0 ? pt[4] : 0;
        float nzNeg = -pt[5] > 0 ? -pt[5] : 0;
        return Shade(baseColor, 0.70f + ny * 0.25f + nzNeg * 0.18f);
    }

    static void AddSphere(List<float> verts, float cx, float cy, float cz,
        float r, float[] baseColor)
    {
        int rings = 8;
        int segs = 16;
        for (int ring = 0; ring < rings; ring++)
        {
            float v0 = -(float)Math.PI * 0.5f + (float)ring / rings * (float)Math.PI;
            float v1 = -(float)Math.PI * 0.5f + (float)(ring + 1) / rings * (float)Math.PI;
            for (int seg = 0; seg < segs; seg++)
            {
                float u0 = (float)seg / segs * (float)Math.PI * 2;
                float u1 = (float)(seg + 1) / segs * (float)Math.PI * 2;

                var a = SpherePoint(cx, cy, cz, r, u0, v0);
                var b = SpherePoint(cx, cy, cz, r, u1, v0);
                var c = SpherePoint(cx, cy, cz, r, u1, v1);
                var d = SpherePoint(cx, cy, cz, r, u0, v1);
                PushVertex(verts, a[0], a[1], a[2], SphereCol(baseColor, a));
                PushVertex(verts, b[0], b[1], b[2], SphereCol(baseColor, b));
                PushVertex(verts, c[0], c[1], c[2], SphereCol(baseColor, c));
                PushVertex(verts, a[0], a[1], a[2], SphereCol(baseColor, a));
                PushVertex(verts, c[0], c[1], c[2], SphereCol(baseColor, c));
                PushVertex(verts, d[0], d[1], d[2], SphereCol(baseColor, d));
            }
        }
    }

    static List<float> BuildVertices()
    {
        var verts = new List<float>();
        AddBox(verts, 0, -0.04f, 0.13f, 2.05f, 1.95f, 0.04f,
            new float[] { 0.05f, 0.07f, 0.11f, 1.0f });
        AddBox(verts, -1.02f, -0.02f, -0.02f, 0.05f, 1.92f, 0.28f,
            new float[] { 0.22f, 0.27f, 0.36f, 1.0f });
        AddBox(verts, 1.02f, -0.02f, -0.02f, 0.05f, 1.92f, 0.28f,
            new float[] { 0.22f, 0.27f, 0.36f, 1.0f });
        AddBox(verts, 0, 0.93f, -0.02f, 2.09f, 0.05f, 0.28f,
            new float[] { 0.22f, 0.27f, 0.36f, 1.0f });

        foreach (var b in bricks)
        {
            if (b.alive)
            {
                AddBox(verts, (b.x0 + b.x1) * 0.5f, (b.y0 + b.y1) * 0.5f, -0.03f,
                    b.x1 - b.x0, b.y1 - b.y0, BRICK_D, rowColors[b.row - 1]);
            }
        }

        AddBox(verts, paddleX, PADDLE_Y, -0.10f, PADDLE_W, PADDLE_H, PADDLE_D,
            new float[] { 0.94f, 0.96f, 0.86f, 1.0f });
        AddSphere(verts, ballX, ballY, -0.20f, BALL_R,
            new float[] { 1.0f, 0.95f, 0.65f, 1.0f });

        for (int i = 1; i <= lives; i++)
        {
            AddSphere(verts, -0.88f + (i - 1) * 0.08f, -0.94f, -0.15f, 0.025f,
                new float[] { 0.95f, 0.32f, 0.36f, 1.0f });
        }
        int scoreShow = score < 12 ? score : 12;
        for (int i = 1; i <= scoreShow; i++)
        {
            AddBox(verts, 0.48f + (i - 1) * 0.04f, -0.94f, -0.12f, 0.022f, 0.055f,
                0.04f, new float[] { 0.26f, 0.82f, 0.62f, 1.0f });
        }

        return verts;
    }

    static List<float> MakeMvp(float t)
    {
        float yaw = -0.22f + (float)Math.Sin(t * 0.35f) * 0.025f;
        float pitch = -0.18f;
        var ry = Mat4.rotateY(-yaw);
        var rx = Mat4.rotateX(-pitch);
        var view = Mat4.translate(new Vec3(0, -0.02f, 3.15f));
        // proj: perspective with focal length f=2.05 directly, aspect=16/9,
        // near=0.1, far=40
        float f = 2.05f;
        float aspect = 16.0f / 9.0f;
        float nz = 0.1f;
        float fz = 40.0f;
        var proj = Mat4.zero();
        proj.m[0] = f / aspect;
        proj.m[5] = f;
        proj.m[10] = fz / (fz - nz);
        proj.m[11] = -fz * nz / (fz - nz);
        proj.m[14] = 1.0f;
        return proj.mul(view.mul(rx.mul(ry))).m;
    }

    public static void onFrame(float dt)
    {
        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.frame(dt, _ =>
        {
            cameraT = cameraT + DT;
            UpdateGame(stepNow.keyPressed("r"));
        });

        Io.load_text("samples/10_breakout3d/data/10_breakout3d.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.load_text("samples/10_breakout3d/data/10_breakout3d.fs.slang",
            out var fs, out var fsv, out _, out _);
        if (vs == null || fs == null) return;

        var verts = BuildVertices();
        var shader = Gfx.use_shader("breakout3d_shader", vs, fs,
            vsv * 31 + fsv);
        var vbuf = Gfx.use_buffer("breakout3d_verts", Gfx.VERTEX, verts);
        if (shader == null || vbuf == null) return;

        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new float[] { 0.025f, 0.032f, 0.048f, 1.0f },
        });
        Gfx.draw(verts.Count / STRIDE,
            new Dictionary<string, object>
            {
                ["verts"] = vbuf,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["mvp"] = MakeMvp(cameraT),
                },
            },
            new DrawOpts
            {
                shader = shader,
                depth = true,
                depth_write = true,
                cull = Gfx.NONE,
            });
        Gfx.end_pass();
    }
}
