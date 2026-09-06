// lub の samples/16_box2d の entry。
// 実行: lub samples/16_box2d/Box2d16.csproj (transpile + watch + hot reload)
// Phys2d の即時モード API で simulation tick ごとに world/body/shape を宣言し、
// 最新 pose を render frame ごとに頂点列へ焼いて 1 draw で描く。
using System;
using System.Collections.Generic;
using static Lub;

public static class Box2d16
{
    const float tickDt = 1.0f / 60.0f;
    const float ppmX = 4.0f;
    const float ppmY = 2.7f;
    static int contactFlash = 0;
    static FixedStep? step = null;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        Lub.Config(new ConfigOpts { Backend = backend, Width = 640, Height = 360 });
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    static void PushVertex(List<float> verts, float x, float y,
        float r, float g, float b, float a)
    {
        verts.Add(x / ppmX);
        verts.Add(y / ppmY);
        verts.Add(0.0f);
        verts.Add(r);
        verts.Add(g);
        verts.Add(b);
        verts.Add(a);
    }

    static void PushBox(List<float> verts, Pose pose, float hx, float hy,
        float[] color)
    {
        float c = (float)Math.Cos(pose.Angle);
        float s = (float)Math.Sin(pose.Angle);
        var cxs = new float[] { -hx, hx, hx, -hx };
        var cys = new float[] { -hy, -hy, hy, hy };
        var wx = new List<float>();
        var wy = new List<float>();
        for (int i = 0; i < 4; i++)
        {
            wx.Add(pose.X + c * cxs[i] - s * cys[i]);
            wy.Add(pose.Y + s * cxs[i] + c * cys[i]);
        }
        var idx = new int[] { 0, 1, 2, 0, 2, 3 };
        foreach (var i in idx)
        {
            PushVertex(verts, wx[i], wy[i],
                color[0], color[1], color[2], color[3]);
        }
    }

    static void Simulate(WorldRef world)
    {
        Phys2d.Begin(world);

        var ground = Phys2d.Body(world, "ground", new BodyDesc
        {
            Type = Phys2d.BodyType.Static,
            Initial = new InitialState { X = 0.0f, Y = -1.55f },
        });
        if (ground == null) return;
        Phys2d.Box(ground, "floor", new BoxDesc
        {
            Hx = 3.4f,
            Hy = 0.18f,
            Density = 0.0f,
            Friction = 0.85f,
            Contact = true,
        });

        for (int i = 0; i < 4; i++)
        {
            bool even = i == 0 || i == 2;
            var b = Phys2d.Body(world, "box:" + i, new BodyDesc
            {
                Type = Phys2d.BodyType.Dynamic,
                Initial = new InitialState
                {
                    X = even ? -0.18f : 0.18f,
                    Y = -0.95f + i * 0.58f,
                    Angle = (i - 1) * 0.08f,
                },
            });
            if (b == null) return;
            Phys2d.Box(b, "solid", new BoxDesc
            {
                Hx = 0.26f,
                Hy = 0.26f,
                Density = 1.0f,
                Friction = 0.65f,
                Contact = true,
            });
        }

        Phys2d.Step(world, tickDt);

        var contacts = Phys2d.Contacts(world, Phys2d.EventKind.Begin);
        if (contacts.Count > 0) contactFlash = 12;
        if (contactFlash > 0) contactFlash = contactFlash - 1;
    }

    public static void OnFrame(float dt)
    {
        var world = Phys2d.World("box2d16", new WorldOpts
        {
            Gravity = new Vec2d { X = 0.0f, Y = -10.0f },
            FixedDt = tickDt,
            Substeps = 4,
            MaxSteps = 1,
        });
        if (world == null) return;

        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.Frame(dt, _ => Simulate(world));

        var verts = new List<float>();
        var groundPose = Phys2d.PoseByKey(world, "ground");
        if (groundPose == null) return;
        PushBox(verts, groundPose, 3.4f, 0.18f,
            new float[] { 0.28f, 0.33f, 0.36f, 1.0f });
        int boxCount = 1;
        for (int i = 0; i < 4; i++)
        {
            var pose = Phys2d.PoseByKey(world, "box:" + i);
            if (pose == null) continue;
            float hot = contactFlash > 0 ? 0.12f : 0.0f;
            PushBox(verts, pose, 0.26f, 0.26f,
                new float[] { 0.20f + hot, 0.62f, 0.88f, 1.0f });
            boxCount = boxCount + 1;
        }

        Io.LoadText("samples/16_box2d/data/16_color.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.LoadText("samples/16_box2d/data/16_color.fs.slang",
            out var fs, out var fsv, out _, out _);
        if (vs == null || fs == null) return;
        var shader = Gfx.UseShader("box2d16_color", vs, fs, vsv * 31 + fsv);
        var mesh = Gfx.UseBuffer("box2d16_mesh", Gfx.BufferType.Vertex, verts);
        if (shader == null || mesh == null) return;

        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.03f, 0.04f, 0.055f, 1.0f },
        });
        Gfx.Draw(boxCount * 6,
            new Dictionary<string, object> { ["verts"] = mesh },
            new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }
}
