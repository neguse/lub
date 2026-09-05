// lub のゲームの雛形 (立方体フラッピーバード)。同じソースが 2 つの実行形で動く:
//   lub Game.csproj                       tcs→Lua (transpile + watch + hot reload)
//   dotnet run                            .NET 実行 (host/Program.cs、P/Invoke)
// data/ への path は project のディレクトリ基準 (そこで起動する)。

using System;
using System.Collections.Generic;
using static Lub;

public static class Game
{
    static float t = 0;
    static float playerY = 0;
    static float velocityY = 0;
    static float pipeX = 5.0f;
    static float gapY = 0;
    static int score = 0;
    static bool dead = false;

    public static void OnInit()
    {
        Config(new ConfigOpts { Width = 1280, Height = 720 });
    }

    // dt は直近 frame の実測秒。固定レート前提にせず dt でスケールする。
    public static void OnFrame(float dt)
    {
        t = t + dt;

        var s = Assets.Shader("cube_shader", "data/cube.vs.slang", "data/cube.fs.slang");
        var b = Assets.Floats("cube_verts", Gfx.BufferType.Vertex, "data/cube.verts.lua");
        if (s == null || b == null) return;

        // KeyPressed / MousePressed は frame でラッチされた edge 検出。
        var flap = Input.KeyPressed("space") || Input.MousePressed();
        if (!dead)
        {
            if (flap) velocityY = 3.0f;
            velocityY = velocityY - 8.0f * dt;
            playerY = playerY + velocityY * dt;

            pipeX = pipeX - 2.0f * dt;
            if (pipeX < -3.0f)
            {
                pipeX = 5.0f;
                gapY = (float)Math.Sin(t * 1.7f) * 1.5f;
                score = score + 1;
            }

            if (playerY < -3.0f || playerY > 3.0f) dead = true;
            if (pipeX > -1.0f && pipeX < 1.0f)
            {
                if (playerY > gapY + 1.0f || playerY < gapY - 1.0f) dead = true;
            }
        }
        else if (flap)
        {
            dead = false;
            playerY = 0;
            velocityY = 0;
            pipeX = 5.0f;
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
            ClearColor = new float[] { 0.05f, 0.05f, 0.15f, 1.0f },
        });
        var drawOpts = new DrawOpts { Shader = s, Depth = true, Cull = Gfx.Cull.None };

        DrawCube(b, vp * Mat4.Translate(new Vec3(-2.0f, playerY, 0)) * Mat4.RotateY(t * 3.0f)
            * Mat4.Scale(new Vec3(0.4f, 0.4f, 0.4f)), drawOpts);
        var pipeScale = Mat4.Scale(new Vec3(0.8f, 5.0f, 0.8f));
        DrawCube(b, vp * Mat4.Translate(new Vec3(pipeX, gapY + 3.5f, 0)) * pipeScale, drawOpts);
        DrawCube(b, vp * Mat4.Translate(new Vec3(pipeX, gapY - 3.5f, 0)) * pipeScale, drawOpts);
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
