// lub の samples/10_breakout3d 相当を TinyC# で書いた entry。
// 実行: lub samples/10_breakout3d/Breakout3d10.csproj (transpile + watch + hot reload)
// gameplay は原典の rule (paddle/ball/bricks, key_down 駆動) を踏襲し、
// 型は Dynamic ではなく class Brick / out 引数の multi-return で表現する。
// MVP とカメラ揺れはフレーム決定的な cameraT から導出する。

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

public static class Breakout3d10
{
    const double dt = 1.0 / 60.0;
    const int stride = 7; // pos.xyz + color.rgba

    const int cols = 9;
    const int rows = 5;
    const double brickGapX = 0.035;
    const double brickGapY = 0.03;
    const double brickLeft = -0.83;
    const double brickRight = 0.83;
    const double brickTop = 0.70;
    const double brickH = 0.075;
    const double brickD = 0.16;
    const double brickW =
        (brickRight - brickLeft - brickGapX * (cols - 1)) / cols;

    const double paddleY = -0.76;
    const double paddleW = 0.38;
    const double paddleH = 0.055;
    const double paddleD = 0.24;
    const double paddleSpeed = 1.55;

    const double ballR = 0.035;
    const double ballSpeedX = 0.58;
    const double ballSpeedY = 0.85;

    static List<double[]> rowColors = new List<double[]>
    {
        new double[] { 0.95, 0.24, 0.28, 1.0 },
        new double[] { 0.98, 0.55, 0.15, 1.0 },
        new double[] { 0.98, 0.86, 0.22, 1.0 },
        new double[] { 0.22, 0.70, 0.40, 1.0 },
        new double[] { 0.16, 0.58, 0.88, 1.0 },
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
    static double cameraT = 0;

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

    static double[] Shade(double[] c, double k)
    {
        return new double[]
        {
            MathUtil.Clamp(c[0] * k, 0, 1),
            MathUtil.Clamp(c[1] * k, 0, 1),
            MathUtil.Clamp(c[2] * k, 0, 1),
            c[3],
        };
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
        ballY = paddleY + paddleH * 0.5 + ballR + 0.015;
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
        paddleX = MathUtil.Clamp(paddleX + move * paddleSpeed * dt,
            -1 + paddleW * 0.5 + 0.05, 1 - paddleW * 0.5 - 0.05);

        if (ballStuck)
        {
            ballX = paddleX;
            ballY = paddleY + paddleH * 0.5 + ballR + 0.015;
            launchTimer = launchTimer + dt;
            if (Input.KeyDown("space") || launchTimer > 1.0)
            {
                LaunchBall();
            }
            return;
        }

        ballX = ballX + ballVx * dt;
        ballY = ballY + ballVy * dt;

        if (ballX - ballR < -0.95)
        {
            ballX = -0.95 + ballR;
            ballVx = Math.Abs(ballVx);
        }
        else if (ballX + ballR > 0.95)
        {
            ballX = 0.95 - ballR;
            ballVx = -Math.Abs(ballVx);
        }
        if (ballY + ballR > 0.88)
        {
            ballY = 0.88 - ballR;
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
            ballVx = MathUtil.Clamp(hit * 0.9 + (paddleX - paddlePrevX) * 2.5,
                -0.98, 0.98);
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

    static void PushVertex(List<double> verts, double x, double y, double z,
        double[] c)
    {
        verts.Add(x);
        verts.Add(y);
        verts.Add(z);
        verts.Add(c[0]);
        verts.Add(c[1]);
        verts.Add(c[2]);
        verts.Add(c[3]);
    }

    static void Quad(List<double> verts, double[] a, double[] b, double[] c,
        double[] d, double[] col)
    {
        PushVertex(verts, a[0], a[1], a[2], col);
        PushVertex(verts, b[0], b[1], b[2], col);
        PushVertex(verts, c[0], c[1], c[2], col);
        PushVertex(verts, a[0], a[1], a[2], col);
        PushVertex(verts, c[0], c[1], c[2], col);
        PushVertex(verts, d[0], d[1], d[2], col);
    }

    static void AddBox(List<double> verts, double cx, double cy, double cz,
        double sx, double sy, double sz, double[] baseColor)
    {
        double x0 = cx - sx * 0.5;
        double x1 = cx + sx * 0.5;
        double y0 = cy - sy * 0.5;
        double y1 = cy + sy * 0.5;
        double z0 = cz - sz * 0.5;
        double z1 = cz + sz * 0.5;

        var p000 = new double[] { x0, y0, z0 };
        var p100 = new double[] { x1, y0, z0 };
        var p010 = new double[] { x0, y1, z0 };
        var p110 = new double[] { x1, y1, z0 };
        var p001 = new double[] { x0, y0, z1 };
        var p101 = new double[] { x1, y0, z1 };
        var p011 = new double[] { x0, y1, z1 };
        var p111 = new double[] { x1, y1, z1 };

        Quad(verts, p000, p100, p110, p010, Shade(baseColor, 1.05));
        Quad(verts, p101, p001, p011, p111, Shade(baseColor, 0.58));
        Quad(verts, p001, p000, p010, p011, Shade(baseColor, 0.72));
        Quad(verts, p100, p101, p111, p110, Shade(baseColor, 0.82));
        Quad(verts, p010, p110, p111, p011, Shade(baseColor, 1.22));
        Quad(verts, p001, p101, p100, p000, Shade(baseColor, 0.48));
    }

    static double[] SpherePoint(double cx, double cy, double cz, double r,
        double u, double vv)
    {
        double cv = Math.Cos(vv);
        return new double[]
        {
            cx + Math.Cos(u) * cv * r,
            cy + Math.Sin(vv) * r,
            cz + Math.Sin(u) * cv * r,
            Math.Cos(u) * cv,
            Math.Sin(vv),
            Math.Sin(u) * cv,
        };
    }

    static double[] SphereCol(double[] baseColor, double[] pt)
    {
        double ny = pt[4] > 0 ? pt[4] : 0;
        double nzNeg = -pt[5] > 0 ? -pt[5] : 0;
        return Shade(baseColor, 0.70 + ny * 0.25 + nzNeg * 0.18);
    }

    static void AddSphere(List<double> verts, double cx, double cy, double cz,
        double r, double[] baseColor)
    {
        int rings = 8;
        int segs = 16;
        for (int ring = 0; ring < rings; ring++)
        {
            double v0 = -Math.PI * 0.5 + (double)ring / rings * Math.PI;
            double v1 = -Math.PI * 0.5 + (double)(ring + 1) / rings * Math.PI;
            for (int seg = 0; seg < segs; seg++)
            {
                double u0 = (double)seg / segs * Math.PI * 2;
                double u1 = (double)(seg + 1) / segs * Math.PI * 2;

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

    static List<double> BuildVertices()
    {
        var verts = new List<double>();
        AddBox(verts, 0, -0.04, 0.13, 2.05, 1.95, 0.04,
            new double[] { 0.05, 0.07, 0.11, 1.0 });
        AddBox(verts, -1.02, -0.02, -0.02, 0.05, 1.92, 0.28,
            new double[] { 0.22, 0.27, 0.36, 1.0 });
        AddBox(verts, 1.02, -0.02, -0.02, 0.05, 1.92, 0.28,
            new double[] { 0.22, 0.27, 0.36, 1.0 });
        AddBox(verts, 0, 0.93, -0.02, 2.09, 0.05, 0.28,
            new double[] { 0.22, 0.27, 0.36, 1.0 });

        foreach (var b in bricks)
        {
            if (b.Alive)
            {
                AddBox(verts, (b.X0 + b.X1) * 0.5, (b.Y0 + b.Y1) * 0.5, -0.03,
                    b.X1 - b.X0, b.Y1 - b.Y0, brickD, rowColors[b.Row - 1]);
            }
        }

        AddBox(verts, paddleX, paddleY, -0.10, paddleW, paddleH, paddleD,
            new double[] { 0.94, 0.96, 0.86, 1.0 });
        AddSphere(verts, ballX, ballY, -0.20, ballR,
            new double[] { 1.0, 0.95, 0.65, 1.0 });

        for (int i = 1; i <= lives; i++)
        {
            AddSphere(verts, -0.88 + (i - 1) * 0.08, -0.94, -0.15, 0.025,
                new double[] { 0.95, 0.32, 0.36, 1.0 });
        }
        int scoreShow = score < 12 ? score : 12;
        for (int i = 1; i <= scoreShow; i++)
        {
            AddBox(verts, 0.48 + (i - 1) * 0.04, -0.94, -0.12, 0.022, 0.055,
                0.04, new double[] { 0.26, 0.82, 0.62, 1.0 });
        }

        return verts;
    }

    static List<double> MakeMvp(double t)
    {
        double yaw = -0.22 + Math.Sin(t * 0.35) * 0.025;
        double pitch = -0.18;
        var ry = Mat4.RotateY(-yaw);
        var rx = Mat4.RotateX(-pitch);
        var view = Mat4.Translate(new Vec3(0, -0.02, 3.15));
        // proj: perspective with focal length f=2.05 directly, aspect=16/9,
        // near=0.1, far=40
        double f = 2.05;
        double aspect = 16.0 / 9.0;
        double nz = 0.1;
        double fz = 40.0;
        var proj = Mat4.Zero();
        proj.M[0] = f / aspect;
        proj.M[5] = f;
        proj.M[10] = fz / (fz - nz);
        proj.M[11] = -fz * nz / (fz - nz);
        proj.M[14] = 1.0;
        return proj.Mul(view.Mul(rx.Mul(ry))).M;
    }

    public static void OnFrame(double dt)
    {
        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.Frame(dt, _ =>
        {
            cameraT = cameraT + dt;
            UpdateGame(stepNow.KeyPressed("r"));
        });

        Io.LoadText("samples/10_breakout3d/data/10_breakout3d.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.LoadText("samples/10_breakout3d/data/10_breakout3d.fs.slang",
            out var fs, out var fsv, out _, out _);
        if (vs == null || fs == null) return;

        var verts = BuildVertices();
        var shader = Gfx.UseShader("breakout3d_shader", vs, fs,
            vsv * 31 + fsv);
        var vbuf = Gfx.UseBuffer("breakout3d_verts", Gfx.BufferType.Vertex, verts);
        if (shader == null || vbuf == null) return;

        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new double[] { 0.025, 0.032, 0.048, 1.0 },
        });
        Gfx.Draw(verts.Count / stride,
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
                Shader = shader,
                Depth = true,
                DepthWrite = true,
                Cull = Gfx.Cull.None,
            });
        Gfx.EndPass();
    }
}
