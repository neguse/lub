// phys2d phase3: CastMover / CollideMover / Explode。visitor が失敗した query は結果なし (null) で返り、visitor 内の physics 変更は拒否される。
using System;
using Lub;

public static class TestPhysicsBox2dPhase3
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 320, Height = 180 });
    }

    static MoverDesc Mover()
    {
        return new MoverDesc
        {
            Ax = 0,
            Ay = 0.1f,
            Bx = 0,
            By = 0.9f,
            R = 0.2f,
            Filter = new FilterDesc { MaskBits = "0000000000000001" },
        };
    }

    public static void OnFrame(float dt)
    {
        var world = Phys2d.World("phase3", new WorldOpts
        {
            Gravity = new Vec2d { X = 0, Y = 0 },
            FixedDt = 1.0f / 60.0f,
            Substeps = 4,
            MaxSteps = 1,
        });
        Phys2d.Begin(world);

        var ground = Phys2d.Body(world, "ground", new BodyDesc
        {
            Type = Phys2d.BodyType.Static,
            Initial = new InitialState { X = 0, Y = -0.2f },
        });
        Phys2d.Box(ground, "solid", new BoxDesc
        {
            Hx = 4,
            Hy = 0.2f,
            Filter = new FilterDesc { CategoryBits = "0000000000000001", MaskBits = "ffffffffffffffff" },
        });

        var ball = Phys2d.Body(world, "ball", new BodyDesc
        {
            Type = Phys2d.BodyType.Dynamic,
            Initial = new InitialState { X = 1, Y = 0.6f },
        });
        Phys2d.Circle(ball, "solid", new CircleDesc
        {
            R = 0.2f,
            Density = 1,
            Filter = new FilterDesc { CategoryBits = "0000000000000002", MaskBits = "ffffffffffffffff" },
        });

        var cast = Phys2d.CastMover(world, new MoverDesc
        {
            Ax = 0,
            Ay = 1.2f,
            Bx = 0,
            By = 2.0f,
            R = 0.2f,
            Dx = 0,
            Dy = -2.0f,
            Filter = new FilterDesc { MaskBits = "0000000000000001" },
        });
        if (cast == null)
        {
            Check.Fail("cast_mover missed ground");
            return;
        }
        Check.That(cast.Fraction > 0 && cast.Fraction < 1, "cast_mover fraction out of range: " + cast.Fraction);
        Check.That(cast.Dy < 0, "cast_mover did not return downward safe delta");

        var visitedPlane = false;
        var planes = Phys2d.CollideMover(world, Mover(), plane =>
        {
            visitedPlane = visitedPlane || plane.Body == "ground";
            return true;
        });
        if (!visitedPlane || planes == null || planes.Count < 1)
        {
            Check.Fail("collide_mover missed ground plane");
            return;
        }
        Check.That(planes[0].Body == "ground", "collide_mover plane snapshot missing shape view");

        var badPlanes = Phys2d.CollideMover(world, Mover(), plane =>
        {
            Phys2d.Pose(null!);
            return true;
        });
        Check.That(badPlanes == null, "collide_mover visitor error did not return nil,error");

        var pending = Phys2d.WorldInfo(world)!.PendingCommands;
        var mutatingPlanes = Phys2d.CollideMover(world, Mover(), plane =>
        {
            Phys2d.SetVelocity(ball, new VelocityDesc { Vx = 0, Vy = 0 });
            return true;
        });
        Check.That(mutatingPlanes == null, "collide_mover visitor mutation was not rejected");
        Check.That(Phys2d.WorldInfo(world)!.PendingCommands == pending, "collide_mover visitor mutation was queued");

        Phys2d.Explode(world, new ExplosionDesc
        {
            X = 0,
            Y = 0.6f,
            Radius = 2,
            Falloff = 0,
            ImpulsePerLength = 20,
            Filter = new FilterDesc { MaskBits = "0000000000000002" },
        });
        Phys2d.Step(world, 1.0f / 60.0f);

        var velocity = Phys2d.Velocity(ball);
        Check.That(velocity.X > 0.1f, "explode did not push ball outward: vx=" + velocity.X);

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.015f, 0.015f, 0.02f, 1.0f } });
        Gfx.EndPass();

        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
