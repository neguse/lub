// lub の samples/17_flappy 相当を TinyC# で書いた entry。
// 実行: lub samples/17_flappy/Flappy17.csproj (transpile + watch + hot reload)
// gameplay は原典の rule (重力 / ジャンプ力 / パイプ間隔 / 当たり判定) を踏襲し、
// Boot.config 相当 (LUB_BACKEND 補完) は直書きする。

using System;
using System.Collections.Generic;

public static class Flappy17
{
    static float t = 0;
    static float playerY = 0;
    static float velocityY = 0;
    static float pipeX = 5.0f;
    static float gapY = 0;
    static int score = 0;
    static bool dead = false;

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND");
        Lub.config(new ConfigOpts { backend = backend });
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    public static void onFrame(float dt)
    {
        t = t + dt;

        var s = Assets.shader("cube_shader",
            "samples/17_flappy/data/cube.vs.slang",
            "samples/17_flappy/data/cube.fs.slang");
        var b = Assets.floats("cube_verts", Gfx.VERTEX,
            "samples/17_flappy/data/cube.verts.lua");
        if (s == null || b == null) return;

        // key_pressed / mouse_pressed はフレームラッチされたエッジ検出。
        // タップ (web) は SDL の合成でマウス左ボタンとして届く。

        var flap = Input.key_pressed("space") || Input.mouse_pressed();
        if (!dead)
        {
            if (flap)
            {
                velocityY = 3.0f;
                Audio.audio_play(Sfx.blip(300, 700, 0.09f, 0.4f));
            }
            velocityY = velocityY - 8.0f * dt;
            playerY = playerY + velocityY * dt;

            pipeX = pipeX - 2.0f * dt;
            if (pipeX < -3.0f)
            {
                pipeX = 5.0f;
                gapY = (float)Math.Sin(t * 1.7f) * 1.5f;
                score = score + 1;
                Audio.audio_play(Sfx.blip(660, 990, 0.12f, 0.35f));
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
                Audio.audio_play(Sfx.noise(0.3f, 0.5f));
            }

            // 落下速度に pitch が追従する風切り音 (毎フレーム宣言する声)。
            // 宣言をやめれば fade out するので stop 管理は要らない。
            var wind = Math.Min(1.0f, Math.Abs(velocityY) * 0.25f);
            Audio.audio_voice("wind", Sfx.noise(0.3f, 0.5f), new VoiceOpts
            {
                loop = true,
                volume = 0.05f * wind,
                pitch = 0.5f + wind,
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

        var vp = Camera3d.vp(new Camera3dOpts
        {
            eye = new Vec3(0, 0, -8),
            target = new Vec3(0, 0, 0),
        });

        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new float[] { 0.05f, 0.05f, 0.15f, 1.0f },
        });

        var drawOpts = new DrawOpts
        {
            shader = s,
            depth = true,
            cull = Gfx.NONE,
        };

        var playerModel = Mat4.translate(new Vec3(-2.0f, playerY, 0))
            * Mat4.rotateY(t * 3.0f) * Mat4.scale(new Vec3(0.4f, 0.4f, 0.4f));
        var playerMvp = vp * playerModel;
        Gfx.draw(36, new Dictionary<string, object>
        {
            ["verts"] = b,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["mvp"] = playerMvp.m,
            },
        }, drawOpts);

        var pipeScale = Mat4.scale(new Vec3(0.8f, 5.0f, 0.8f));
        var topModel = Mat4.translate(new Vec3(pipeX, gapY + 3.5f, 0))
            * pipeScale;
        var topMvp = vp * topModel;
        Gfx.draw(36, new Dictionary<string, object>
        {
            ["verts"] = b,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["mvp"] = topMvp.m,
            },
        }, drawOpts);

        var botModel = Mat4.translate(new Vec3(pipeX, gapY - 3.5f, 0))
            * pipeScale;
        var botMvp = vp * botModel;
        Gfx.draw(36, new Dictionary<string, object>
        {
            ["verts"] = b,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["mvp"] = botMvp.m,
            },
        }, drawOpts);

        Gfx.end_pass();
    }
}
