// lub の samples/17_flappy 相当を TinyC# で書いた entry。
// 実行: lub samples/17_flappy/Flappy17.csproj (transpile + watch + hot reload)
// gameplay は原典の rule (重力 / ジャンプ力 / パイプ間隔 / 当たり判定) を踏襲し、
// Boot.config 相当 (LUB_BACKEND 補完) は直書きする。

using System;
using System.Collections.Generic;
using static Lub;

public static class Flappy17
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
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        Lub.Config(new ConfigOpts { Backend = backend });
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    public static void OnFrame(float dt)
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
                velocityY = 3.0f;
                Audio.Play(Sfx.Blip(300, 700, 0.09f, 0.4f));
            }
            velocityY = velocityY - 8.0f * dt;
            playerY = playerY + velocityY * dt;

            pipeX = pipeX - 2.0f * dt;
            if (pipeX < -3.0f)
            {
                pipeX = 5.0f;
                gapY = (float)Math.Sin(t * 1.7f) * 1.5f;
                score = score + 1;
                Audio.Play(Sfx.Blip(660, 990, 0.12f, 0.35f));
            }

            if (playerY < -3.0f || playerY > 3.0f)
            {
                dead = true;
            }
            if (pipeX > -1.0f && pipeX < 1.0f)
            {
                if (playerY > gapY + 1.0f || playerY < gapY - 1.0f)
                {
                    dead = true;
                }
            }
            if (dead)
            {
                Audio.Play(Sfx.Noise(0.3f, 0.5f));
            }

            // 落下速度に pitch が追従する風切り音 (毎フレーム宣言する声)。
            // 宣言をやめれば fade out するので stop 管理は要らない。
            var wind = Math.Min(1.0f, Math.Abs(velocityY) * 0.25f);
            Audio.Voice("wind", Sfx.Noise(0.3f, 0.5f), new VoiceOpts
            {
                Loop = true,
                Volume = 0.05f * wind,
                Pitch = 0.5f + wind,
            });
        }
        else
        {
            if (flap)
            {
                dead = false;
                playerY = 0;
                velocityY = 0;
                pipeX = 5.0f;
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
            ClearColor = new float[] { 0.05f, 0.05f, 0.15f, 1.0f },
        });

        var drawOpts = new DrawOpts
        {
            Shader = s,
            Depth = true,
            Cull = Gfx.Cull.None,
        };

        var playerModel = Mat4.Translate(new Vec3(-2.0f, playerY, 0))
            * Mat4.RotateY(t * 3.0f) * Mat4.Scale(new Vec3(0.4f, 0.4f, 0.4f));
        var playerMvp = vp * playerModel;
        Gfx.Draw(36, new Dictionary<string, object>
        {
            ["verts"] = b,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["mvp"] = playerMvp.M,
            },
        }, drawOpts);

        var pipeScale = Mat4.Scale(new Vec3(0.8f, 5.0f, 0.8f));
        var topModel = Mat4.Translate(new Vec3(pipeX, gapY + 3.5f, 0))
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

        var botModel = Mat4.Translate(new Vec3(pipeX, gapY - 3.5f, 0))
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
