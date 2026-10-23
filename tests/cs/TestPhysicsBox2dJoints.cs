// Phys2d の joint (distance / revolute / prismatic / wheel / weld / motor / mouse / filter) の宣言、set 系の更新、readback、body の joint 列挙、counters。
using System;
using Lub;

public static class TestPhysicsBox2dJoints
{
    static WorldRef? world;

    static bool HasJoint(System.Collections.Generic.List<JointView> items, string key, Phys2d.JointType kind)
    {
        foreach (var item in items)
        {
            if (item.Joint == key && item.Type == kind && item.Valid)
                return true;
        }
        return false;
    }

    static BodyRef DynamicBody(string key, float x, float y)
    {
        var b = Phys2d.Body(world!, key, new BodyDesc { Type = Phys2d.BodyType.Dynamic, Initial = new InitialState { X = x, Y = y } });
        Phys2d.Circle(b!, "solid", new CircleDesc { R = 0.12f, Density = 1 });
        return b!;
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 320, Height = 180 });
    }

    public static void OnFrame(float dt)
    {
        world = Phys2d.World("joints", new WorldOpts
        {
            Gravity = new Vec2d { X = 0, Y = 0 },
            FixedDt = 1.0f / 60.0f,
            Substeps = 4,
            MaxSteps = 1,
        });
        Phys2d.Begin(world!);

        var ground = Phys2d.Body(world!, "ground", new BodyDesc { Type = Phys2d.BodyType.Static, Initial = new InitialState { X = 0, Y = 0 } });
        Phys2d.Box(ground!, "solid", new BoxDesc { Hx = 0.2f, Hy = 0.2f });

        var bob = DynamicBody("bob", 0, 1);
        var rotor = DynamicBody("rotor", 1, 0);
        var slider = DynamicBody("slider", -1, 0);
        var wheel = DynamicBody("wheel", -1, 1);
        var panel = DynamicBody("panel", 1, 1);
        var motorBody = DynamicBody("motor_body", -2, 1);
        var mouseBody = DynamicBody("mouse_body", 2, 1);
        var filterBody = DynamicBody("filter_body", 2, 0);

        var distance = Phys2d.Joint(world!, "distance", new JointDesc
        {
            Type = Phys2d.JointType.Distance,
            BodyA = ground,
            BodyB = bob,
            Length = 1.0f,
            Spring = new JointSpringDesc { Enabled = true, Hertz = 2.0f, DampingRatio = 0.7f },
            Motor = new JointMotorDesc { Enabled = true, Speed = 0.0f, MaxForce = 5.0f },
        });
        var hinge = Phys2d.Joint(world!, "hinge", new JointDesc
        {
            Type = Phys2d.JointType.Revolute,
            BodyA = ground,
            BodyB = rotor,
            Motor = new JointMotorDesc { Enabled = true, Speed = 1.0f, MaxTorque = 3.0f },
            Limit = new JointLimitDesc { Enabled = true, Lower = -0.5f, Upper = 0.5f },
        });
        var prismatic = Phys2d.Joint(world!, "slider_joint", new JointDesc
        {
            Type = Phys2d.JointType.Prismatic,
            BodyA = ground,
            BodyB = slider,
            LocalAxisA = new Vec2d { X = 1, Y = 0 },
            Limit = new JointLimitDesc { Enabled = true, Lower = -0.5f, Upper = 0.5f },
            Motor = new JointMotorDesc { Enabled = true, Speed = 0.1f, MaxForce = 4.0f },
        });
        Phys2d.Joint(world!, "wheel_joint", new JointDesc
        {
            Type = Phys2d.JointType.Wheel,
            BodyA = ground,
            BodyB = wheel,
            LocalAxisA = new Vec2d { X = 0, Y = 1 },
            Spring = new JointSpringDesc { Enabled = true, Hertz = 2.0f, DampingRatio = 0.7f },
        });
        Phys2d.Joint(world!, "weld_joint", new JointDesc
        {
            Type = Phys2d.JointType.Weld,
            BodyA = ground,
            BodyB = panel,
            Spring = new JointSpringDesc { LinearHertz = 2.0f, AngularHertz = 2.0f },
        });
        Phys2d.Joint(world!, "motor_joint", new JointDesc
        {
            Type = Phys2d.JointType.Motor,
            BodyA = ground,
            BodyB = motorBody,
            LinearOffset = new Vec2d { X = 0.1f, Y = 0.1f },
            MaxForce = 5.0f,
            MaxTorque = 5.0f,
        });
        Phys2d.Joint(world!, "mouse_joint", new JointDesc
        {
            Type = Phys2d.JointType.Mouse,
            BodyA = ground,
            BodyB = mouseBody,
            Target = new Vec2d { X = 2.1f, Y = 1.0f },
            Hertz = 2.0f,
            MaxForce = 5.0f,
        });
        Phys2d.Joint(world!, "filter_joint", new JointDesc
        {
            Type = Phys2d.JointType.Filter,
            BodyA = ground,
            BodyB = filterBody,
        });

        Phys2d.JointSetMotor(hinge!, new JointMotorDesc { Enabled = true, Speed = 0.5f, MaxTorque = 4.0f });
        Phys2d.JointSetLimit(hinge!, new JointLimitDesc { Enabled = true, Lower = -0.6f, Upper = 0.6f });
        Phys2d.JointSetSpring(distance!, new JointSpringDesc { Enabled = true, Hertz = 3.0f, DampingRatio = 0.8f });
        Phys2d.JointSetTarget(prismatic!, new JointTargetDesc { Translation = 0.1f });

        var info = Phys2d.JointInfo(hinge!);
        if (info == null || info.Type != Phys2d.JointType.Revolute || info.A != "ground" || info.B != "rotor")
        {
            Check.Fail("joint info missing hinge endpoint data");
            return;
        }
        Check.That(Phys2d.JointAngle(hinge!) != null, "revolute angle readback missing");
        Check.That(Phys2d.JointMotorTorque(hinge!) != null, "revolute motor torque readback missing");
        Check.That(Phys2d.JointLength(distance!) != null, "distance length readback missing");
        Check.That(Phys2d.JointTranslation(prismatic!) != null && Phys2d.JointSpeed(prismatic!) != null, "prismatic readback missing");
        Check.That(HasJoint(Phys2d.BodyJoints(ground!), "hinge", Phys2d.JointType.Revolute), "body joint enumeration missing hinge");

        Phys2d.Step(world!, 1.0f / 60.0f);
        var counters = Phys2d.Counters(world!);
        Check.That(counters != null && counters.JointCount >= 8, "expected 8 joints, got " + (counters == null ? -1 : counters.JointCount));

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.015f, 0.015f, 0.02f, 1.0f } });
        Gfx.EndPass();
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
