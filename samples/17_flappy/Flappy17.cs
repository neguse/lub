// Entry equivalent to lub's samples/17_flappy, written in TinyC#.
// Run: lub samples/17_flappy/Flappy17.csproj (transpile + watch + hot reload)

using System;
using System.Collections.Generic;
using Lub;
using Lubx;

public static class Flappy17
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
    static float pipeX = 5.0f;
    static float gapY = 0;
    static int score = 0;
    static bool dead = false;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        App.Config(new ConfigOpts { Backend = backend });
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
        var b = Gfx.UseBuffer("cube_verts", Gfx.BufferType.Storage, cubeVerts);
        if (s == null || b == null) return;

        // key_pressed / mouse_pressed are frame-latched edge detection.
        // A tap (web) arrives as the left mouse button, synthesized by SDL.

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

            // Wind noise whose pitch follows the falling speed (a voice declared every frame).
            // Stopping the declaration fades it out, so no stop management is needed.
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
            * Mat4.RotateY(-t * 3.0f) * Mat4.Scale(new Vec3(0.4f, 0.4f, 0.4f));
        var playerMvp = vp * playerModel;
        var bindings = new Bindings();
        bindings.Set("verts", b);
        bindings.Set("mvp", playerMvp);
        Gfx.Draw(36, bindings, drawOpts);

        var pipeScale = Mat4.Scale(new Vec3(0.8f, 5.0f, 0.8f));
        var topModel = Mat4.Translate(new Vec3(pipeX, gapY + 3.5f, 0))
            * pipeScale;
        var topMvp = vp * topModel;
        bindings.Clear();
        bindings.Set("verts", b);
        bindings.Set("mvp", topMvp);
        Gfx.Draw(36, bindings, drawOpts);

        var botModel = Mat4.Translate(new Vec3(pipeX, gapY - 3.5f, 0))
            * pipeScale;
        var botMvp = vp * botModel;
        bindings.Clear();
        bindings.Set("verts", b);
        bindings.Set("mvp", botMvp);
        Gfx.Draw(36, bindings, drawOpts);

        Gfx.EndPass();
    }
}
