// lub のゲームの雛形 (立方体フラッピーバード)。同じソースが 2 つの実行形で動く:
//   lub Game.csproj                       tcs→Lua (transpile + watch + hot reload)
//   dotnet run                            .NET 実行 (host/Program.cs、P/Invoke)
// data/ への path は project のディレクトリ基準 (そこで起動する)。

using System;
using System.Collections.Generic;
using static Lub;

public static class Game
{
    static double t = 0;
    static double playerY = 0;
    static double velocityY = 0;
    static double pipeX = 5.0;
    static double gapY = 0;
    static int score = 0;
    static bool dead = false;

    public static void OnInit()
    {
        Config(new ConfigOpts { Width = 1280, Height = 720 });
    }

    // dt は直近 frame の実測秒。固定レート前提にせず dt でスケールする。
    public static void OnFrame(double dt)
    {
        t = t + dt;

        var s = Assets.Shader("cube_shader", "data/cube.vs.slang", "data/cube.fs.slang");
        var b = Assets.Floats("cube_verts", Gfx.BufferType.Vertex, "data/cube.verts.lua");
        if (s == null || b == null) return;

        // KeyPressed / MousePressed は frame でラッチされた edge 検出。
        var flap = Input.KeyPressed("space") || Input.MousePressed();
        if (!dead)
        {
            if (flap) velocityY = 3.0;
            velocityY = velocityY - 8.0 * dt;
            playerY = playerY + velocityY * dt;

            pipeX = pipeX - 2.0 * dt;
            if (pipeX < -3.0)
            {
                pipeX = 5.0;
                gapY = Math.Sin(t * 1.7) * 1.5;
                score = score + 1;
            }

            if (playerY < -3.0 || playerY > 3.0) dead = true;
            if (pipeX > -1.0 && pipeX < 1.0)
            {
                if (playerY > gapY + 1.0 || playerY < gapY - 1.0) dead = true;
            }
        }
        else if (flap)
        {
            dead = false;
            playerY = 0;
            velocityY = 0;
            pipeX = 5.0;
            score = 0;
        }

        var vp = Camera3d.Vp(new Camera3dOpts
        {
            Eye = new Vec3(0, 0, -8),
            Target = new Vec3(0, 0, 0),
        });

        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new double[] { 0.05, 0.05, 0.15, 1.0 },
        });
        var drawOpts = new DrawOpts { Shader = s, Depth = true, Cull = Gfx.Cull.None };

        DrawCube(b, vp * Mat4.Translate(new Vec3(-2.0, playerY, 0)) * Mat4.RotateY(t * 3.0)
            * Mat4.Scale(new Vec3(0.4, 0.4, 0.4)), drawOpts);
        var pipeScale = Mat4.Scale(new Vec3(0.8, 5.0, 0.8));
        DrawCube(b, vp * Mat4.Translate(new Vec3(pipeX, gapY + 3.5, 0)) * pipeScale, drawOpts);
        DrawCube(b, vp * Mat4.Translate(new Vec3(pipeX, gapY - 3.5, 0)) * pipeScale, drawOpts);
        Gfx.EndPass();
    }

    static void DrawCube(BufferRef verts, Mat4 mvp, DrawOpts opts)
    {
        Gfx.Draw(36, new Dictionary<string, object>
        {
            ["verts"] = verts,
            ["uniforms"] = new Dictionary<string, object> { ["mvp"] = mvp.M },
        }, opts);
    }
}
