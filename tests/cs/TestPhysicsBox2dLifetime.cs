// Phys2d の body / shape / chain / joint の寿命。Begin の prune、version による再作成、消えた対象への stale な ref (null を返す)、消えた contact / sensor の end event が tombstone key を保つこと。
using System;
using System.Collections.Generic;
using Lub;

public static class TestPhysicsBox2dLifetime
{
    static int frame;
    static BodyRef? keptRef;
    static ShapeRef? keptShapeRef;
    static ChainRef? staleChainRef;
    static BodyRef? recreateRef;
    static JointRef? staleJointRef;

    static bool PartMatches(ShapeView part, string body, string shape)
    {
        return part.Body == body && part.Shape == shape;
    }

    static bool DestroyedPair(ContactEvent e, string lhsBody, string lhsShape, string rhsBody, string rhsShape)
    {
        var direct = PartMatches(e.A, lhsBody, lhsShape) && PartMatches(e.B, rhsBody, rhsShape);
        var swapped = PartMatches(e.B, lhsBody, lhsShape) && PartMatches(e.A, rhsBody, rhsShape);
        if (!direct && !swapped)
            return false;
        return e.A.Valid == false && e.B.Valid == false;
    }

    static bool DestroyedSensor(SensorEvent e, string sensorBody, string sensorShape, string visitorBody, string visitorShape)
    {
        return PartMatches(e.Sensor, sensorBody, sensorShape)
            && PartMatches(e.Visitor, visitorBody, visitorShape)
            && e.Sensor.Valid == false
            && e.Visitor.Valid == false;
    }

    static BodyDesc Dyn(float x, float y, int? version = null)
    {
        return new BodyDesc { Version = version, Type = Phys2d.BodyType.Dynamic, Initial = new InitialState { X = x, Y = y } };
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 320, Height = 180 });
    }

    public static void OnFrame(float dt)
    {
        frame++;

        var world = Phys2d.World("lifetime", new WorldOpts
        {
            Gravity = new Vec2d { X = 0, Y = 0 },
            FixedDt = 1.0f / 60.0f,
            Substeps = 4,
            MaxSteps = 1,
        });

        if (frame == 1)
        {
            Phys2d.Begin(world!, new BeginOpts { Prune = false });
            var kept = Phys2d.Body(world!, "kept", Dyn(1, 2));
            keptRef = kept;
            keptShapeRef = Phys2d.Box(kept!, "solid", new BoxDesc { Hx = 0.1f, Hy = 0.1f });
            var recreate = Phys2d.Body(world!, "recreate", Dyn(3, 0, 1));
            recreateRef = recreate;
            Phys2d.Box(recreate!, "solid", new BoxDesc { Hx = 0.1f, Hy = 0.1f });
            var jointA = Phys2d.Body(world!, "joint_a", Dyn(10, 0));
            var jointB = Phys2d.Body(world!, "joint_b", Dyn(12, 0));
            staleJointRef = Phys2d.Joint(world!, "stale_joint", new JointDesc
            {
                Type = Phys2d.JointType.Distance,
                BodyA = jointA,
                BodyB = jointB,
                Length = 2,
            });
            var versionedJointA = Phys2d.Body(world!, "versioned_joint_a", Dyn(14, 0));
            var versionedJointB = Phys2d.Body(world!, "versioned_joint_b", Dyn(16, 0));
            Phys2d.Joint(world!, "versioned_joint", new JointDesc
            {
                Version = 1,
                Type = Phys2d.JointType.Distance,
                BodyA = versionedJointA,
                BodyB = versionedJointB,
                Length = 2,
                CollideConnected = false,
            });
            var chainBody = Phys2d.Body(world!, "chain_body", new BodyDesc { Type = Phys2d.BodyType.Static, Initial = new InitialState { X = 0, Y = -2 } });
            staleChainRef = Phys2d.Chain(chainBody!, "terrain", new ChainDesc
            {
                Version = 1,
                Points = new List<float> { -2, 0, -1, 0, 1, 0, 2, 0 },
            });
            Phys2d.Body(world!, "hash_body", Dyn(-1, 0));
            Phys2d.Body(world!, "explicit_body", Dyn(-3, 0, 1));
            var versionedShapeBody = Phys2d.Body(world!, "versioned_shape", new BodyDesc { Type = Phys2d.BodyType.Static, Initial = new InitialState { X = -5, Y = 0 } });
            Phys2d.Box(versionedShapeBody!, "solid", new BoxDesc
            {
                Version = 1,
                Hx = 0.2f,
                Hy = 0.2f,
                Contact = false,
                Hit = false,
                PreSolve = false,
                SensorEvents = false,
            });

            var wall = Phys2d.Body(world!, "wall", new BodyDesc { Type = Phys2d.BodyType.Static, Initial = new InitialState { X = 0, Y = 0 } });
            Phys2d.Box(wall!, "solid", new BoxDesc { Hx = 0.3f, Hy = 0.3f, Contact = true, MaterialName = "wall", MaterialId = 10 });
            var crate = Phys2d.Body(world!, "crate", Dyn(0, 0));
            Phys2d.Box(crate!, "solid", new BoxDesc { Hx = 0.2f, Hy = 0.2f, Contact = true, MaterialName = "crate", MaterialId = 11 });

            var sensor = Phys2d.Body(world!, "sensor", new BodyDesc { Type = Phys2d.BodyType.Static, Initial = new InitialState { X = 1, Y = 0 } });
            Phys2d.Box(sensor!, "zone", new BoxDesc { Hx = 0.3f, Hy = 0.3f, Sensor = true, SensorEvents = true, MaterialName = "zone", MaterialId = 12 });
            var visitor = Phys2d.Body(world!, "visitor", Dyn(1, 0));
            Phys2d.Box(visitor!, "solid", new BoxDesc { Hx = 0.2f, Hy = 0.2f, SensorEvents = true, MaterialName = "visitor", MaterialId = 13 });

            Phys2d.Step(world!, 1.0f / 60.0f);
        }
        else if (frame == 2)
        {
            Phys2d.Begin(world!, new BeginOpts { Prune = false });
            Phys2d.SetVelocity(recreateRef!, new VelocityDesc { Vx = 5, Vy = 0 });
            var recreated = Phys2d.Body(world!, "recreate", Dyn(3, 0, 2));
            Phys2d.Box(recreated!, "solid", new BoxDesc { Hx = 0.1f, Hy = 0.1f });
            Phys2d.Body(world!, "hash_body", Dyn(-2, 0));
            Phys2d.Body(world!, "explicit_body", Dyn(-4, 0, 1));
            var versionedJointA = Phys2d.Body(world!, "versioned_joint_a", Dyn(14, 0));
            var versionedJointB = Phys2d.Body(world!, "versioned_joint_b", Dyn(16, 0));
            var versionedJoint = Phys2d.Joint(world!, "versioned_joint", new JointDesc
            {
                Version = 1,
                Type = Phys2d.JointType.Distance,
                BodyA = versionedJointA,
                BodyB = versionedJointB,
                Length = 2,
                CollideConnected = true,
            });
            var versionedShapeBody = Phys2d.Body(world!, "versioned_shape", new BodyDesc { Type = Phys2d.BodyType.Static, Initial = new InitialState { X = -5, Y = 0 } });
            var versionedShape = Phys2d.Box(versionedShapeBody!, "solid", new BoxDesc
            {
                Version = 1,
                Hx = 0.5f,
                Hy = 0.2f,
                Contact = true,
                Hit = true,
                PreSolve = true,
                SensorEvents = true,
            });
            var info = Phys2d.Step(world!, 1.0f / 60.0f);
            Check.That(info.Commands == 0, "queued command applied to recreated body");
            var recreatedPose = Phys2d.PoseByKey(world!, "recreate");
            Check.That(recreatedPose != null && Math.Abs(recreatedPose.Vx) <= 0.001f, "stale command mutated recreated body");
            var hashPose = Phys2d.PoseByKey(world!, "hash_body");
            Check.That(hashPose != null && hashPose.X >= -2.01f && hashPose.X <= -1.99f, "omitted body version did not hash initial state");
            var explicitPose = Phys2d.PoseByKey(world!, "explicit_body");
            Check.That(explicitPose != null && explicitPose.X >= -3.01f && explicitPose.X <= -2.99f, "explicit body version did not preserve initial state");
            var versionedJointInfo = Phys2d.JointInfo(versionedJoint!);
            Check.That(versionedJointInfo != null && !versionedJointInfo.CollideConnected, "explicit joint version applied constructor-only collide_connected");
            var versionedAabb = Phys2d.ShapeAabb(versionedShape!);
            Check.That(versionedAabb != null && versionedAabb.MaxX - versionedAabb.MinX <= 0.45f, "explicit shape version recreated changed geometry");
            var versionedInfo = Phys2d.ShapeInfo(versionedShape!);
            Check.That(
                versionedInfo != null && versionedInfo.Contact && versionedInfo.Hit && versionedInfo.PreSolve && versionedInfo.SensorEvents,
                "explicit shape version did not apply runtime event flags");
            var pose = Phys2d.PoseByKey(world!, "kept");
            Check.That(pose != null && pose.X >= 0.99f && pose.X <= 1.01f, "prune=false did not preserve undeclared body");
        }
        else if (frame == 3)
        {
            Phys2d.Begin(world!);
            Phys2d.SetVelocity(keptRef!, new VelocityDesc { Vx = 4, Vy = 0 });
            var info = Phys2d.Step(world!, 1.0f / 60.0f);
            Check.That(info.Commands == 0, "queued command applied to pruned body");
            Check.That(Phys2d.PoseByKey(world!, "kept") == null, "prune=true did not remove undeclared body");
            Check.That(Phys2d.Velocity(keptRef!) == null, "stale body velocity did not return not found");
            Check.That(Phys2d.BodyShapes(keptRef!) == null, "stale body shapes did not return not found");
            Check.That(Phys2d.ShapeAabb(keptShapeRef!) == null, "stale shape aabb did not return not found");
            Check.That(Phys2d.ChainSegments(staleChainRef!) == null, "stale chain segments did not return not found");
            Check.That(Phys2d.JointInfo(staleJointRef!) == null, "stale joint info did not return not found");
            var sawContactEnd = false;
            foreach (var e in Phys2d.Contacts(world!, Phys2d.EventKind.End))
                sawContactEnd = sawContactEnd || DestroyedPair(e, "wall", "solid", "crate", "solid");
            Check.That(sawContactEnd, "destroyed contact end did not retain tombstone keys");
            var sawSensorEnd = false;
            foreach (var e in Phys2d.Sensors(world!, Phys2d.EventKind.End))
                sawSensorEnd = sawSensorEnd || DestroyedSensor(e, "sensor", "zone", "visitor", "solid");
            Check.That(sawSensorEnd, "destroyed sensor end did not retain tombstone keys");
            Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.015f, 0.015f, 0.02f, 1.0f } });
            Gfx.EndPass();
            Check.Pass();
            return;
        }

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.015f, 0.015f, 0.02f, 1.0f } });
        Gfx.EndPass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
