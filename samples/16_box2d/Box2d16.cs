// lub の samples/16_box2d (Haxe 版 Box2d16.hx) の TinyC# 版 entry。
// 実行: lub samples/16_box2d/Box2d16.csproj (transpile + watch + hot reload)
// Phys2d の即時モード API で simulation tick ごとに world/body/shape を宣言し、
// 最新 pose を render frame ごとに頂点列へ焼いて 1 draw で描く。
using System;
using System.Collections.Generic;
using static Lub;

public static class Box2d16
{
    const double dt = 1.0 / 60.0;
    const double ppmX = 4.0;
    const double ppmY = 2.7;
    static int contactFlash = 0;
    static FixedStep? step = null;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "native";
        Lub.Config(new ConfigOpts { Backend = backend, Width = 640, Height = 360 });
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    static void PushVertex(List<double> verts, double x, double y,
        double r, double g, double b, double a)
    {
        verts.Add(x / ppmX);
        verts.Add(y / ppmY);
        verts.Add(0.0);
        verts.Add(r);
        verts.Add(g);
        verts.Add(b);
        verts.Add(a);
    }

    static void PushBox(List<double> verts, Pose pose, double hx, double hy,
        double[] color)
    {
        double c = Math.Cos(pose.Angle);
        double s = Math.Sin(pose.Angle);
        var cxs = new double[] { -hx, hx, hx, -hx };
        var cys = new double[] { -hy, -hy, hy, hy };
        var wx = new List<double>();
        var wy = new List<double>();
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
            Initial = new InitialState { X = 0.0, Y = -1.55 },
        });
        if (ground == null) return;
        Phys2d.Box(ground, "floor", new BoxDesc
        {
            Hx = 3.4,
            Hy = 0.18,
            Density = 0.0,
            Friction = 0.85,
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
                    X = even ? -0.18 : 0.18,
                    Y = -0.95 + i * 0.58,
                    Angle = (i - 1) * 0.08,
                },
            });
            if (b == null) return;
            Phys2d.Box(b, "solid", new BoxDesc
            {
                Hx = 0.26,
                Hy = 0.26,
                Density = 1.0,
                Friction = 0.65,
                Contact = true,
            });
        }

        Phys2d.Step(world, dt);

        var contacts = Phys2d.Contacts(world, "begin");
        if (contacts.Count > 0) contactFlash = 12;
        if (contactFlash > 0) contactFlash = contactFlash - 1;
    }

    public static void OnFrame(double dt)
    {
        var world = Phys2d.World("box2d16", new WorldOpts
        {
            Gravity = new Vec2d { X = 0.0, Y = -10.0 },
            FixedDt = dt,
            Substeps = 4,
            MaxSteps = 1,
        });
        if (world == null) return;

        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.Frame(dt, _ => Simulate(world));

        var verts = new List<double>();
        var groundPose = Phys2d.Pose(world, "ground");
        if (groundPose == null) return;
        PushBox(verts, groundPose, 3.4, 0.18,
            new double[] { 0.28, 0.33, 0.36, 1.0 });
        int boxCount = 1;
        for (int i = 0; i < 4; i++)
        {
            var pose = Phys2d.Pose(world, "box:" + i);
            if (pose == null) continue;
            double hot = contactFlash > 0 ? 0.12 : 0.0;
            PushBox(verts, pose, 0.26, 0.26,
                new double[] { 0.20 + hot, 0.62, 0.88, 1.0 });
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
            ClearColor = new double[] { 0.03, 0.04, 0.055, 1.0 },
        });
        Gfx.Draw(boxCount * 6,
            new Dictionary<string, object> { ["verts"] = mesh },
            new DrawOpts { Shader = shader, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }
}
