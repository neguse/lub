// phys2d の smoke: 玉が落ち、地面との contact begin が取れ、body から contact を列挙できる。
using System;
using Lub;

public static class TestPhysicsBox2d
{
    static int frame = 0;
    static bool sawContact = false;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 320, Height = 180 });
    }

    public static void OnFrame(float dt)
    {
        frame = frame + 1;

        var world = Phys2d.World("smoke", new WorldOpts
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
            Initial = new InitialState { X = 0, Y = -0.25f },
        });
        Phys2d.Box(ground, "solid", new BoxDesc
        {
            Hx = 4,
            Hy = 0.25f,
            Density = 0,
            Friction = 0.8f,
            Contact = true,
        });

        var ball = Phys2d.Body(world, "ball", new BodyDesc
        {
            Type = Phys2d.BodyType.Dynamic,
            Initial = new InitialState { X = 0, Y = 2.0f },
        });
        Phys2d.Circle(ball, "solid", new CircleDesc
        {
            R = 0.25f,
            Density = 1,
            Friction = 0.4f,
            Contact = true,
        });

        Phys2d.Step(world, 1.0f / 60.0f);

        var pose = Phys2d.Pose(ball);
        if (pose == null)
        {
            Check.Fail("pose missing");
            return;
        }

        var contacts = Phys2d.Contacts(world, Phys2d.EventKind.Begin);
        foreach (var contact in contacts)
        {
            var a = contact.A.Body + "/" + contact.A.Shape;
            var b = contact.B.Body + "/" + contact.B.Shape;
            if ((a == "ball/solid" && b == "ground/solid") || (a == "ground/solid" && b == "ball/solid"))
                sawContact = true;
        }
        if (sawContact && Phys2d.BodyContacts(ball).Count == 0)
        {
            Check.Fail("body contact enumeration missing ball contact");
            return;
        }

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.02f, 0.03f, 0.04f, 1.0f } });
        Gfx.EndPass();

        if (frame == 30 && !(pose.Y < 1.0f))
        {
            Check.Fail("ball did not fall by frame 30: y=" + pose.Y);
            return;
        }

        if (sawContact)
        {
            Check.Pass();
            return;
        }

        if (frame > 180)
            Check.Fail("contact begin not observed");
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
