// lub のゲームの雛形 (立方体フラッピーバード)。
// 同じソースが 2 つの実行形で動く:
//   lub Game.csproj                       tcs VM の player (bytecode に変換 + watch + hot reload)
//   dotnet run                            .NET 実行 (host/Program.cs、P/Invoke)
// data/ への path は project のディレクトリ基準 (そこで起動する)。

using System;
using System.Collections.Generic;
using Lub;
using Lubx;

// data を運ぶ型は readonly record struct にする (readonly なら copy を省ける)。
public readonly record struct Pipe(float X, float GapY);

public static class Game
{
    // Unit cube: 36 vertices (12 triangles), each vertex = pos(3) + pad(1) + color(4)
    // (16-byte aligned for StructuredBuffer).
    // Front face (blue), Back (red), Top (green), Bottom (yellow), Right (cyan), Left (magenta)
    static List<float> cubeVerts = new List<float>
    {
        // Front (z=+0.5) blue
        -0.5f, -0.5f,  0.5f, 0.0f, 0.2f, 0.3f, 1.0f, 1.0f,
         0.5f, -0.5f,  0.5f, 0.0f, 0.2f, 0.3f, 1.0f, 1.0f,
         0.5f,  0.5f,  0.5f, 0.0f, 0.2f, 0.3f, 1.0f, 1.0f,
        -0.5f, -0.5f,  0.5f, 0.0f, 0.2f, 0.3f, 1.0f, 1.0f,
         0.5f,  0.5f,  0.5f, 0.0f, 0.2f, 0.3f, 1.0f, 1.0f,
        -0.5f,  0.5f,  0.5f, 0.0f, 0.2f, 0.3f, 1.0f, 1.0f,
        // Back (z=-0.5) red
         0.5f, -0.5f, -0.5f, 0.0f, 1.0f, 0.3f, 0.2f, 1.0f,
        -0.5f, -0.5f, -0.5f, 0.0f, 1.0f, 0.3f, 0.2f, 1.0f,
        -0.5f,  0.5f, -0.5f, 0.0f, 1.0f, 0.3f, 0.2f, 1.0f,
         0.5f, -0.5f, -0.5f, 0.0f, 1.0f, 0.3f, 0.2f, 1.0f,
        -0.5f,  0.5f, -0.5f, 0.0f, 1.0f, 0.3f, 0.2f, 1.0f,
         0.5f,  0.5f, -0.5f, 0.0f, 1.0f, 0.3f, 0.2f, 1.0f,
        // Top (y=+0.5) green
        -0.5f,  0.5f,  0.5f, 0.0f, 0.2f, 1.0f, 0.3f, 1.0f,
         0.5f,  0.5f,  0.5f, 0.0f, 0.2f, 1.0f, 0.3f, 1.0f,
         0.5f,  0.5f, -0.5f, 0.0f, 0.2f, 1.0f, 0.3f, 1.0f,
        -0.5f,  0.5f,  0.5f, 0.0f, 0.2f, 1.0f, 0.3f, 1.0f,
         0.5f,  0.5f, -0.5f, 0.0f, 0.2f, 1.0f, 0.3f, 1.0f,
        -0.5f,  0.5f, -0.5f, 0.0f, 0.2f, 1.0f, 0.3f, 1.0f,
        // Bottom (y=-0.5) yellow
        -0.5f, -0.5f, -0.5f, 0.0f, 1.0f, 1.0f, 0.2f, 1.0f,
         0.5f, -0.5f, -0.5f, 0.0f, 1.0f, 1.0f, 0.2f, 1.0f,
         0.5f, -0.5f,  0.5f, 0.0f, 1.0f, 1.0f, 0.2f, 1.0f,
        -0.5f, -0.5f, -0.5f, 0.0f, 1.0f, 1.0f, 0.2f, 1.0f,
         0.5f, -0.5f,  0.5f, 0.0f, 1.0f, 1.0f, 0.2f, 1.0f,
        -0.5f, -0.5f,  0.5f, 0.0f, 1.0f, 1.0f, 0.2f, 1.0f,
        // Right (x=+0.5) cyan
         0.5f, -0.5f,  0.5f, 0.0f, 0.2f, 1.0f, 1.0f, 1.0f,
         0.5f, -0.5f, -0.5f, 0.0f, 0.2f, 1.0f, 1.0f, 1.0f,
         0.5f,  0.5f, -0.5f, 0.0f, 0.2f, 1.0f, 1.0f, 1.0f,
         0.5f, -0.5f,  0.5f, 0.0f, 0.2f, 1.0f, 1.0f, 1.0f,
         0.5f,  0.5f, -0.5f, 0.0f, 0.2f, 1.0f, 1.0f, 1.0f,
         0.5f,  0.5f,  0.5f, 0.0f, 0.2f, 1.0f, 1.0f, 1.0f,
        // Left (x=-0.5) magenta
        -0.5f, -0.5f, -0.5f, 0.0f, 1.0f, 0.2f, 1.0f, 1.0f,
        -0.5f, -0.5f,  0.5f, 0.0f, 1.0f, 0.2f, 1.0f, 1.0f,
        -0.5f,  0.5f,  0.5f, 0.0f, 1.0f, 0.2f, 1.0f, 1.0f,
        -0.5f, -0.5f, -0.5f, 0.0f, 1.0f, 0.2f, 1.0f, 1.0f,
        -0.5f,  0.5f,  0.5f, 0.0f, 1.0f, 0.2f, 1.0f, 1.0f,
        -0.5f,  0.5f, -0.5f, 0.0f, 1.0f, 0.2f, 1.0f, 1.0f,
    };

    static float t = 0;
    static float playerY = 0;
    static float velocityY = 0;
    static Pipe pipe = new Pipe(5.0f, 0);
    static int score = 0;
    static bool dead = false;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 1280, Height = 720 });
    }

    // `lub export` のときだけ、OnInit の後に 1 回呼ばれる (OnFrame は呼ばれない)。
    // ここで使った shader と file が配る形 (web なら page 1 枚) に入る。
    public static void OnExport()
    {
        Assets.Shader("cube_shader", "data/cube.vs.slang", "data/cube.fs.slang");
    }

    // dt は直近 frame の実測秒。
    // 固定レート前提にせず dt でスケールする。
    public static void OnFrame(float dt)
    {
        t = t + dt;

        var s = Assets.Shader("cube_shader", "data/cube.vs.slang", "data/cube.fs.slang");
        var b = Gfx.UseBuffer("cube_verts", Gfx.BufferType.Storage, cubeVerts);
        if (s == null || b == null) return;

        // KeyPressed / MousePressed は frame でラッチされた edge 検出。
        var flap = Input.KeyPressed("space") || Input.MousePressed();
        if (!dead)
        {
            if (flap) velocityY = 3.0f;
            velocityY = velocityY - 8.0f * dt;
            playerY = playerY + velocityY * dt;

            pipe = pipe with { X = pipe.X - 2.0f * dt };
            if (pipe.X < -3.0f)
            {
                pipe = new Pipe(5.0f, (float)Math.Sin(t * 1.7f) * 1.5f);
                score = score + 1;
            }

            if (playerY < -3.0f || playerY > 3.0f) dead = true;
            if (pipe.X > -1.0f && pipe.X < 1.0f)
            {
                if (playerY > pipe.GapY + 1.0f || playerY < pipe.GapY - 1.0f) dead = true;
            }
        }
        else if (flap)
        {
            dead = false;
            playerY = 0;
            velocityY = 0;
            pipe = pipe with { X = 5.0f };
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

        DrawCube(b, vp * Mat4.Translate(new Vec3(-2.0f, playerY, 0)) * Mat4.RotateY(-t * 3.0f)
            * Mat4.Scale(new Vec3(0.4f, 0.4f, 0.4f)), drawOpts);
        var pipeScale = Mat4.Scale(new Vec3(0.8f, 5.0f, 0.8f));
        DrawCube(b, vp * Mat4.Translate(new Vec3(pipe.X, pipe.GapY + 3.5f, 0)) * pipeScale, drawOpts);
        DrawCube(b, vp * Mat4.Translate(new Vec3(pipe.X, pipe.GapY - 3.5f, 0)) * pipeScale, drawOpts);
        Gfx.EndPass();
    }

    static void DrawCube(BufferRef verts, Mat4 mvp, DrawOpts opts)
    {
        var bindings = new Bindings();
        bindings.Set("verts", verts);
        bindings.Set("mvp", mvp);
        Gfx.Draw(36, bindings, opts);
    }
}
