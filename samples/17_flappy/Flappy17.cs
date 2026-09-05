// lub の samples/17_flappy 相当を TinyC# で書いた entry。
// 実行: lub samples/17_flappy/Flappy17.csproj (transpile + watch + hot reload)
// gameplay は原典の rule (重力 / ジャンプ力 / パイプ間隔 / 当たり判定) を踏襲し、
// Boot.config 相当 (LUB_BACKEND 補完) は直書きする。

using System;
using System.Collections.Generic;
using static Lub;

public static class Flappy17
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
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "native";
        Lub.Config(new ConfigOpts { Backend = backend });
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    public static void OnFrame(double dt)
    {
        t = t + dt;

        var s = Assets.Shader("cube_shader",
            "samples/17_flappy/data/cube.vs.slang",
            "samples/17_flappy/data/cube.fs.slang");
        var b = Assets.Floats("cube_verts", Gfx.BufferType.Vertex,
            "samples/17_flappy/data/cube.verts.lua");
        if (s == null || b == null) return;

        // key_pressed / mouse_pressed はフレームラッチされたエッジ検出。
        // タップ (web) は SDL の合成でマウス左ボタンとして届く。

        var flap = Input.KeyPressed("space") || Input.MousePressed();
        if (!dead)
        {
            if (flap)
            {
                velocityY = 3.0;
                Audio.Play(Sfx.Blip(300, 700, 0.09, 0.4));
            }
            velocityY = velocityY - 8.0 * dt;
            playerY = playerY + velocityY * dt;

            pipeX = pipeX - 2.0 * dt;
            if (pipeX < -3.0)
            {
                pipeX = 5.0;
                gapY = Math.Sin(t * 1.7) * 1.5;
                score = score + 1;
                Audio.Play(Sfx.Blip(660, 990, 0.12, 0.35));
            }

            if (playerY < -3.0 || playerY > 3.0)
            {
                dead = true;
            }
            if (pipeX > -1.0 && pipeX < 1.0)
            {
                if (playerY > gapY + 1.0 || playerY < gapY - 1.0)
                {
                    dead = true;
                }
            }
            if (dead)
            {
                Audio.Play(Sfx.Noise(0.3, 0.5));
            }

            // 落下速度に pitch が追従する風切り音 (毎フレーム宣言する声)。
            // 宣言をやめれば fade out するので stop 管理は要らない。
            var wind = Math.Min(1.0, Math.Abs(velocityY) * 0.25);
            Audio.Voice("wind", Sfx.Noise(0.3, 0.5), new VoiceOpts
            {
                Loop = true,
                Volume = 0.05 * wind,
                Pitch = 0.5 + wind,
            });
        }
        else
        {
            if (flap)
            {
                dead = false;
                playerY = 0;
                velocityY = 0;
                pipeX = 5.0;
                score = 0;
            }
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

        var drawOpts = new DrawOpts
        {
            Shader = s,
            Depth = true,
            Cull = Gfx.Cull.None,
        };

        var playerModel = Mat4.Translate(new Vec3(-2.0, playerY, 0))
            * Mat4.RotateY(t * 3.0) * Mat4.Scale(new Vec3(0.4, 0.4, 0.4));
        var playerMvp = vp * playerModel;
        Gfx.Draw(36, new Dictionary<string, object>
        {
            ["verts"] = b,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["mvp"] = playerMvp.M,
            },
        }, drawOpts);

        var pipeScale = Mat4.Scale(new Vec3(0.8, 5.0, 0.8));
        var topModel = Mat4.Translate(new Vec3(pipeX, gapY + 3.5, 0))
            * pipeScale;
        var topMvp = vp * topModel;
        Gfx.Draw(36, new Dictionary<string, object>
        {
            ["verts"] = b,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["mvp"] = topMvp.M,
            },
        }, drawOpts);

        var botModel = Mat4.Translate(new Vec3(pipeX, gapY - 3.5, 0))
            * pipeScale;
        var botMvp = vp * botModel;
        Gfx.Draw(36, new Dictionary<string, object>
        {
            ["verts"] = b,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["mvp"] = botMvp.M,
            },
        }, drawOpts);

        Gfx.EndPass();
    }
}
