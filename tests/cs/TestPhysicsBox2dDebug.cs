// phys2d の debug draw データ (polygons/circles/capsules/segments)、profile、counters が取れる。
using System;
using Lub;

public static class TestPhysicsBox2dDebug
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 320, Height = 180 });
    }

    public static void OnFrame(float dt)
    {
        var world = Phys2d.World("debug", new WorldOpts
        {
            Gravity = new Vec2d { X = 0, Y = -10 },
            FixedDt = 1.0f / 60.0f,
            Substeps = 4,
            MaxSteps = 1,
        });
        Phys2d.Begin(world);

        var ground = Phys2d.Body(world, "ground", new BodyDesc
        {
            Type = Phys2d.BodyType.Static,
            Initial = new InitialState { X = 0, Y = -1 },
        });
        Phys2d.Box(ground, "box", new BoxDesc { Hx = 2, Hy = 0.1f, Density = 0 });
        Phys2d.Segment(ground, "rail", new SegmentDesc { Ax = -2, Ay = 0.3f, Bx = 2, By = 0.3f });

        var ball = Phys2d.Body(world, "ball", new BodyDesc
        {
            Type = Phys2d.BodyType.Dynamic,
            Initial = new InitialState { X = -0.5f, Y = 1 },
        });
        Phys2d.Circle(ball, "circle", new CircleDesc { R = 0.2f, Density = 1 });

        var capsuleBody = Phys2d.Body(world, "capsule", new BodyDesc
        {
            Type = Phys2d.BodyType.Dynamic,
            Initial = new InitialState { X = 0.6f, Y = 1.2f },
        });
        Phys2d.Capsule(capsuleBody, "capsule", new CapsuleDesc
        {
            Ax = -0.2f,
            Ay = 0,
            Bx = 0.2f,
            By = 0,
            R = 0.1f,
            Density = 1,
        });

        Phys2d.Step(world, 1.0f / 60.0f);

        var dbg = Phys2d.Debug(world, new DebugOpts { Shapes = true, Bounds = true, Mass = true });
        if (dbg == null)
        {
            Check.Fail("debug result missing");
            return;
        }
        Check.That(dbg.Polygons.Count >= 10, "expected polygon data, got " + dbg.Polygons.Count);
        Check.That(dbg.Circles.Count >= 7, "expected circle data, got " + dbg.Circles.Count);
        Check.That(dbg.Capsules.Count >= 9, "expected capsule data, got " + dbg.Capsules.Count);
        Check.That(dbg.Segments.Count >= 8, "expected segment data, got " + dbg.Segments.Count);
        var profile = Phys2d.Profile(world);
        if (profile == null || profile.Step < 0)
        {
            Check.Fail("profile step missing");
            return;
        }
        var counters = Phys2d.Counters(world);
        if (counters == null)
        {
            Check.Fail("counters missing");
            return;
        }
        Check.That(counters.BodyCount >= 3, "expected body counters, got " + counters.BodyCount);
        Check.That(counters.ShapeCount >= 4, "expected shape counters, got " + counters.ShapeCount);

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.01f, 0.015f, 0.02f, 1.0f } });
        Gfx.EndPass();

        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
