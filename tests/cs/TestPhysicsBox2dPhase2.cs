// phys2d phase2: world/shape/body の info の round-trip、runtime 宣言 (awake/enabled/sleep)、
// command の遅延適用、chain、sensor/body event、raycast/overlap/shape_cast の query、
// visitor の失敗 (nil で返る) と visitor 内の physics 変更の拒否、shape の runtime setter。
using System;
using System.Collections.Generic;
using Lub;

public static class TestPhysicsBox2dPhase2
{
    static int frame = 0;
    static bool sawSensor = false;
    static bool sawBodyMove = false;
    static bool checkedQueries = false;

    static bool Near(float a, float b)
    {
        return Math.Abs(a - b) < 0.001f;
    }

    static bool HasShape(List<ShapeView> items, string body, string shape)
    {
        foreach (var item in items)
        {
            if (item.Body == body && item.Shape == shape && item.Valid)
                return true;
        }
        return false;
    }

    static RaycastDesc Ray()
    {
        return new RaycastDesc
        {
            X = 0,
            Y = 1,
            Dx = 0,
            Dy = -2,
            Filter = new FilterDesc { MaskBits = "0000000000000001" },
        };
    }

    static AabbDesc Overlap()
    {
        return new AabbDesc
        {
            MinX = -0.4f,
            MinY = -0.4f,
            MaxX = 0.4f,
            MaxY = 0.4f,
            Filter = new FilterDesc { MaskBits = "0000000000000004" },
        };
    }

    static ShapeCastDesc BoxCast()
    {
        return new ShapeCastDesc
        {
            Kind = Phys2d.ProxyKind.Box,
            X = 0,
            Y = 1,
            Hx = 0.1f,
            Hy = 0.1f,
            Dx = 0,
            Dy = -2,
            Filter = new FilterDesc { MaskBits = "0000000000000001" },
        };
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 320, Height = 180 });
    }

    public static void OnFrame(float dt)
    {
        frame = frame + 1;

        var world = Phys2d.World("phase2", new WorldOpts
        {
            Gravity = new Vec2d { X = 0, Y = 0 },
            FixedDt = 1.0f / 60.0f,
            Substeps = 4,
            MaxSteps = 1,
        });
        if (frame == 1)
        {
            var noBeginInfo = Phys2d.Step(world, 0);
            if (noBeginInfo.Steps != 0 || noBeginInfo.Commands != 0)
            {
                Check.Fail("step without begin did not return empty StepInfo");
                return;
            }
        }
        Phys2d.Begin(world);
        var worldInfo = Phys2d.WorldInfo(world);
        if (worldInfo == null || worldInfo.Gravity == null
            || !worldInfo.Valid
            || worldInfo.Key != "phase2"
            || !worldInfo.Begun
            || !worldInfo.Prune
            || !Near(worldInfo.FixedDt, 1.0f / 60.0f)
            || worldInfo.Substeps != 4
            || worldInfo.MaxSteps != 1
            || !Near(worldInfo.Gravity.X, 0)
            || !Near(worldInfo.Gravity.Y, 0)
            || worldInfo.Continuous != true
            || worldInfo.Sleep != true)
        {
            Check.Fail("world info did not round-trip declaration");
            return;
        }

        var ground = Phys2d.Body(world, "ground", new BodyDesc
        {
            Type = Phys2d.BodyType.Static,
            Initial = new InitialState { X = 0, Y = 0 },
        })!;
        var floorShape = Phys2d.Segment(ground, "floor", new SegmentDesc
        {
            Ax = -3,
            Ay = -0.5f,
            Bx = 3,
            By = -0.5f,
            Filter = new FilterDesc { CategoryBits = "0000000000000001", MaskBits = "ffffffffffffffff" },
        })!;

        var sensor = Phys2d.Body(world, "sensor", new BodyDesc
        {
            Type = Phys2d.BodyType.Static,
            Initial = new InitialState { X = 0, Y = 0 },
        })!;
        var sensorShape = Phys2d.Box(sensor, "zone", new BoxDesc
        {
            Hx = 0.3f,
            Hy = 0.35f,
            Sensor = true,
            SensorEvents = true,
            Filter = new FilterDesc { CategoryBits = "0000000000000004", MaskBits = "ffffffffffffffff" },
        })!;

        var rock = Phys2d.Body(world, "rock", new BodyDesc
        {
            Type = Phys2d.BodyType.Static,
            Initial = new InitialState { X = 2, Y = 0 },
        })!;
        var rockShape = Phys2d.Polygon(rock, "hull", new PolygonDesc
        {
            Points = new List<float> { -0.3f, -0.2f, 0.35f, -0.15f, 0.2f, 0.25f, -0.25f, 0.3f },
            Radius = 0.01f,
            Filter = new FilterDesc { CategoryBits = "0000000000000008", MaskBits = "ffffffffffffffff" },
        })!;

        var terrain = Phys2d.Body(world, "terrain", new BodyDesc
        {
            Type = Phys2d.BodyType.Static,
            Initial = new InitialState { X = 0, Y = 0 },
        })!;
        var terrainChain = Phys2d.Chain(terrain, "path", new ChainDesc
        {
            Version = 1,
            Points = new List<float> { -3, -0.8f, -1, -0.8f, 1, -0.8f, 3, -0.8f },
            MaterialName = "terrain",
            Materials = new List<ChainMaterial>
            {
                new ChainMaterial { MaterialId = 9, Friction = 0.7f },
                new ChainMaterial { MaterialId = 9, Friction = 0.7f },
                new ChainMaterial { MaterialId = 9, Friction = 0.7f },
                new ChainMaterial { MaterialId = 9, Friction = 0.7f },
            },
            Friction = 0.7f,
            Filter = new FilterDesc { CategoryBits = "0000000000000010", MaskBits = "ffffffffffffffff" },
        })!;

        var mover = Phys2d.Body(world, "mover", new BodyDesc
        {
            Type = Phys2d.BodyType.Dynamic,
            FixedRotation = true,
            Initial = new InitialState { X = -1, Y = 0 },
        })!;
        var sleepy = Phys2d.Body(world, "sleepy", new BodyDesc
        {
            Type = Phys2d.BodyType.Dynamic,
            Awake = false,
            Initial = new InitialState { X = -2.5f, Y = 1.0f },
        })!;
        Phys2d.Box(sleepy, "solid", new BoxDesc { Hx = 0.1f, Hy = 0.1f, Density = 1 });
        var disabled = Phys2d.Body(world, "disabled", new BodyDesc
        {
            Type = Phys2d.BodyType.Dynamic,
            Enabled = false,
            Initial = new InitialState { X = -2.5f, Y = 1.5f },
        })!;
        Phys2d.Box(disabled, "solid", new BoxDesc { Hx = 0.1f, Hy = 0.1f, Density = 1 });
        var nosleep = Phys2d.Body(world, "nosleep", new BodyDesc
        {
            Type = Phys2d.BodyType.Dynamic,
            Sleep = false,
            SleepThreshold = 0.2f,
            Initial = new InitialState { X = -2.8f, Y = 2.0f },
        })!;
        Phys2d.Box(nosleep, "solid", new BoxDesc { Hx = 0.1f, Hy = 0.1f, Density = 1 });
        var moverShape = Phys2d.Capsule(mover, "capsule", new CapsuleDesc
        {
            Ax = -0.15f,
            Ay = 0,
            Bx = 0.15f,
            By = 0,
            R = 0.1f,
            Density = 1,
            Tag = "player",
            MaterialName = "player",
            MaterialId = 7,
            Contact = true,
            Hit = true,
            SensorEvents = true,
            PreSolve = true,
            Filter = new FilterDesc { CategoryBits = "0000000000000002", MaskBits = "ffffffffffffffff" },
        })!;
        var preCommandVelocity = Phys2d.Velocity(mover);
        Phys2d.AddImpulseCenter(mover, new Vec2d { X = 0.001f, Y = 0 });
        Phys2d.SetVelocity(mover, new VelocityDesc { Vx = 2.0f, Vy = 0 });
        Phys2d.SetMassData(mover, new MassDataDesc
        {
            Mass = 2.0f,
            Inertia = 1.0f,
            LocalCenter = new Vec2d { X = 0.05f, Y = 0 },
        });
        var queuedVelocity = Phys2d.Velocity(mover);
        if (!Near(queuedVelocity.X, preCommandVelocity.X) || !Near(queuedVelocity.Y, preCommandVelocity.Y))
        {
            Check.Fail("queued velocity command applied before step");
            return;
        }
        var queuedInfo = Phys2d.WorldInfo(world)!;
        if (queuedInfo.PendingCommands != 3)
        {
            Check.Fail("unexpected pending command count: " + queuedInfo.PendingCommands);
            return;
        }

        var info = Phys2d.Step(world, 1.0f / 60.0f);
        if (info.Steps != 1)
        {
            Check.Fail("unexpected step count: " + info.Steps);
            return;
        }
        if (info.Commands != 3)
        {
            Check.Fail("unexpected applied command count: " + info.Commands);
            return;
        }
        var sleepyPose = Phys2d.Pose(sleepy)!;
        if (sleepyPose.Awake != false)
        {
            Check.Fail("body awake runtime declaration did not apply");
            return;
        }
        var disabledPose = Phys2d.Pose(disabled)!;
        if (disabledPose.Enabled != false)
        {
            Check.Fail("body enabled runtime declaration did not apply");
            return;
        }
        var nosleepPose = Phys2d.Pose(nosleep)!;
        if (nosleepPose.Sleep != false || !Near(nosleepPose.SleepThreshold, 0.2f))
        {
            Check.Fail("body sleep runtime declaration did not apply");
            return;
        }
        if (info.BodyEvents != info.BodyMoves)
        {
            Check.Fail("StepInfo body_events alias mismatch");
            return;
        }
        var chainSegments = Phys2d.ChainSegments(terrainChain);
        if (chainSegments.Count != 1 || chainSegments[0].Chain != "path" || chainSegments[0].Kind != Phys2d.ShapeKind.ChainSegment)
        {
            Check.Fail("chain segment enumeration missing terrain segment");
            return;
        }
        if (chainSegments[0].MaterialName != "terrain" || chainSegments[0].MaterialId != 9)
        {
            Check.Fail("chain segment metadata/material missing: material=" + chainSegments[0].MaterialName
                + " material_id=" + chainSegments[0].MaterialId);
            return;
        }
        var terrainShapes = Phys2d.BodyShapes(terrain);
        if (terrainShapes.Count != 1 || terrainShapes[0].Kind != Phys2d.ShapeKind.ChainSegment)
        {
            Check.Fail("body shape enumeration missing chain segment");
            return;
        }
        var moverShapes = Phys2d.BodyShapes(mover);
        if (moverShapes.Count != 1 || moverShapes[0].Shape != "capsule" || moverShapes[0].Kind != Phys2d.ShapeKind.Capsule)
        {
            Check.Fail("body shape enumeration missing mover capsule");
            return;
        }
        var moverInfo = Phys2d.ShapeInfo(moverShape)!;
        var floorInfo = Phys2d.ShapeInfo(floorShape);
        if (floorInfo == null || !Near(floorInfo.Density, 0))
        {
            Check.Fail("static shape default density was not zero");
            return;
        }
        if (!moverInfo.Valid
            || moverInfo.Body != "mover"
            || moverInfo.Shape != "capsule"
            || moverInfo.Kind != Phys2d.ShapeKind.Capsule
            || moverInfo.Tag != "player"
            || moverInfo.MaterialName != "player"
            || moverInfo.MaterialId != 7)
        {
            Check.Fail("shape info identity/material metadata missing");
            return;
        }
        if (!Near(moverInfo.Density, 1)
            || moverInfo.Sensor
            || !moverInfo.Contact
            || !moverInfo.Hit
            || !moverInfo.SensorEvents
            || !moverInfo.PreSolve)
        {
            Check.Fail("shape info physical/event flags missing");
            return;
        }
        if (moverInfo.Filter.CategoryBits != "0000000000000002"
            || moverInfo.Filter.MaskBits != "ffffffffffffffff"
            || moverInfo.Filter.Group != 0)
        {
            Check.Fail("shape info filter missing");
            return;
        }
        if (moverInfo.Aabb.MinX >= moverInfo.Aabb.MaxX)
        {
            Check.Fail("shape info aabb missing");
            return;
        }
        if (!Phys2d.ShapeTestPoint(sensorShape, new Vec2d { X = 0, Y = 0 }))
        {
            Check.Fail("shape test_point missed sensor center");
            return;
        }
        if (!Phys2d.ShapeTestPoint(rockShape, new Vec2d { X = 2, Y = 0 }))
        {
            Check.Fail("polygon test_point missed rock center");
            return;
        }
        var sensorAabb = Phys2d.ShapeAabb(sensorShape)!;
        if (sensorAabb.MinX > -0.29f || sensorAabb.MaxX < 0.29f)
        {
            Check.Fail("shape aabb missing sensor extents");
            return;
        }
        var closest = Phys2d.ShapeClosestPoint(sensorShape, new Vec2d { X = 1, Y = 0 });
        if (closest.X < 0.25f || closest.X > 0.36f)
        {
            Check.Fail("shape closest_point unexpected x=" + closest.X);
            return;
        }
        var shapeHit = Phys2d.ShapeRaycast(floorShape, new RaycastDesc { X = 0, Y = 1, Dx = 0, Dy = -2 });
        if (shapeHit == null || shapeHit.Y > -0.49f || shapeHit.Y < -0.51f)
        {
            Check.Fail("shape raycast missed floor");
            return;
        }
        var velocity = Phys2d.Velocity(mover);
        if (velocity.X <= 0)
        {
            Check.Fail("velocity readback did not move right");
            return;
        }
        var mass = Phys2d.Mass(mover)!;
        if (!Near(mass.Mass, 2.0f))
        {
            Check.Fail("mass readback missing");
            return;
        }
        if (!Near(mass.Inertia, 1.0f) || !Near(mass.LocalCenter.X, 0.05f))
        {
            Check.Fail("mass data override did not round-trip");
            return;
        }
        var center = Phys2d.Center(mover);
        var worldOrigin = Phys2d.WorldPoint(mover, new Vec2d { X = 0, Y = 0 });
        var localOrigin = Phys2d.LocalPoint(mover, worldOrigin);
        if (!Near(localOrigin.X, 0) || !Near(localOrigin.Y, 0))
        {
            Check.Fail("point conversion did not round-trip");
            return;
        }
        var velocityAtCenter = Phys2d.VelocityAt(mover, center);
        if (velocityAtCenter.X <= 0)
        {
            Check.Fail("point velocity readback missing");
            return;
        }

        foreach (var ev in Phys2d.BodyEvents(world))
        {
            if (ev.Body == "mover" && ev.Valid)
                sawBodyMove = true;
        }

        foreach (var ev in Phys2d.Sensors(world, Phys2d.EventKind.Begin))
        {
            if (ev.Sensor.Body == "sensor" && ev.Sensor.Shape == "zone" && ev.Visitor.Body == "mover")
                sawSensor = true;
        }

        if (!checkedQueries)
        {
            var hit = Phys2d.Raycast(world, Ray());
            if (hit == null || hit.Body != "ground" || hit.Shape != "floor")
            {
                Check.Fail("closest raycast missed ground");
                return;
            }

            var visited = false;
            var hits = Phys2d.RaycastAll(world, Ray(), visitorHit =>
            {
                visited = visited || visitorHit.Body == "ground";
                return visitorHit.Fraction;
            });
            if (!visited || hits == null || hits.Count < 1)
            {
                Check.Fail("visitor raycast missed ground");
                return;
            }
            var badRaycast = Phys2d.RaycastAll(world, Ray(), visitorHit =>
            {
                Phys2d.Pose(null!);
                return visitorHit.Fraction;
            });
            if (badRaycast != null)
            {
                Check.Fail("raycast visitor error did not return nil,error");
                return;
            }
            var pending = Phys2d.WorldInfo(world)!.PendingCommands;
            var mutatingRaycast = Phys2d.RaycastAll(world, Ray(), visitorHit =>
            {
                Phys2d.SetVelocity(mover, new VelocityDesc { Vx = 0, Vy = 0 });
                return visitorHit.Fraction;
            });
            if (mutatingRaycast != null || Phys2d.WorldInfo(world)!.PendingCommands != pending)
            {
                Check.Fail("raycast visitor mutation was not rejected");
                return;
            }

            var overlaps = Phys2d.OverlapAabb(world, Overlap());
            if (!HasShape(overlaps, "sensor", "zone"))
            {
                Check.Fail("overlap_aabb missed sensor zone");
                return;
            }
            var badOverlap = Phys2d.OverlapAabb(world, Overlap(), shape =>
            {
                Phys2d.Pose(null!);
                return true;
            });
            if (badOverlap != null)
            {
                Check.Fail("overlap_aabb visitor error did not return nil,error");
                return;
            }
            var cast = Phys2d.ShapeCast(world, new ShapeCastDesc
            {
                Kind = Phys2d.ProxyKind.Circle,
                X = 0,
                Y = 1,
                Radius = 0.1f,
                Dx = 0,
                Dy = -2,
                Filter = new FilterDesc { MaskBits = "0000000000000001" },
            });
            if (cast == null || cast.Body != "ground" || cast.Shape != "floor")
            {
                Check.Fail("closest shape_cast missed ground");
                return;
            }
            var shapeCastVisited = false;
            var shapeCastHits = Phys2d.ShapeCastAll(world, BoxCast(), castHit =>
            {
                shapeCastVisited = shapeCastVisited || castHit.Body == "ground";
                return castHit.Fraction;
            });
            if (!shapeCastVisited || shapeCastHits == null || shapeCastHits.Count < 1)
            {
                Check.Fail("visitor shape_cast missed ground");
                return;
            }
            var badShapeCast = Phys2d.ShapeCastAll(world, BoxCast(), castHit =>
            {
                Phys2d.Pose(null!);
                return castHit.Fraction;
            });
            if (badShapeCast != null)
            {
                Check.Fail("shape_cast visitor error did not return nil,error");
                return;
            }
            checkedQueries = true;
        }

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.02f, 0.02f, 0.025f, 1.0f } });
        Gfx.EndPass();

        if (sawSensor && sawBodyMove && checkedQueries)
        {
            Phys2d.ShapeSetMaterial(moverShape, new MaterialDesc
            {
                MaterialName = "runtime",
                MaterialId = 11,
                Friction = 0.25f,
                Restitution = 0.4f,
                Density = 1.25f,
            });
            Phys2d.ShapeSetFilter(moverShape, new FilterDesc { CategoryBits = "0000000000000020", MaskBits = "0000000000000020" });
            Phys2d.ShapeSetEvents(moverShape, new ShapeEventsDesc
            {
                Contact = false,
                Hit = false,
                SensorEvents = false,
                PreSolve = false,
            });
            var runtimeInfo = Phys2d.ShapeInfo(moverShape)!;
            if (runtimeInfo.MaterialName != "runtime"
                || runtimeInfo.MaterialId != 11
                || !Near(runtimeInfo.Friction, 0.25f)
                || !Near(runtimeInfo.Restitution, 0.4f)
                || !Near(runtimeInfo.Density, 1.25f))
            {
                Check.Fail("shape runtime material setter did not round-trip");
                return;
            }
            if (runtimeInfo.Filter.CategoryBits != "0000000000000020" || runtimeInfo.Filter.MaskBits != "0000000000000020")
            {
                Check.Fail("shape runtime filter setter did not round-trip");
                return;
            }
            if (runtimeInfo.Contact || runtimeInfo.Hit || runtimeInfo.SensorEvents || runtimeInfo.PreSolve)
            {
                Check.Fail("shape runtime event setter did not round-trip");
                return;
            }
            Check.Pass();
            return;
        }

        if (frame > 120)
        {
            var pose = Phys2d.Pose(mover)!;
            Check.Fail("sensor/body events not observed sensor=" + sawSensor + " body_move=" + sawBodyMove + " x=" + pose.X);
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
