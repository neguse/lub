// visitor の中から同じ (か結果の buffer を共有する) query を呼んでも、外側の結果が内側の結果で上書きされない。
using System;
using System.Collections.Generic;
using Lub;

public static class TestPhysicsNestedQuery
{
    static readonly List<int> innerCounts = new List<int>();

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    static void Expect(string label, int got, int want)
    {
        Check.That(got == want, label + ": got " + got + ", want " + want);
    }

    // 外側の結果の数と、内側の各呼び出しの結果の数を確かめて innerCounts を空にする。
    static void Nested(string label, int outer, int wantOuter, int wantInner)
    {
        Expect(label + " outer", outer, wantOuter);
        Expect(label + " inner calls", innerCounts.Count, wantOuter);
        foreach (var n in innerCounts)
            Expect(label + " inner", n, wantInner);
        innerCounts.Clear();
    }

    static RaycastDesc Ray(float y)
    {
        return new RaycastDesc { X = 0, Y = y, Dx = 5, Dy = 0 };
    }

    static ShapeCastDesc Cast(float y)
    {
        return new ShapeCastDesc { Kind = Phys2d.ProxyKind.Circle, Radius = 0.05f, X = 0, Y = y, Dx = 5, Dy = 0 };
    }

    // y = 0 の列に 4 個、y = 5 に 1 個の box。外側の query は y = 0 の列を、内側の query は y = 5 の 1 個を拾う。
    static void Check2d()
    {
        var world = Phys2d.World("nested2d", new WorldOpts { Gravity = new Vec2d { X = 0, Y = 0 } });
        Phys2d.Begin(world!);
        for (int i = 1; i <= 4; i++)
        {
            var row = Phys2d.Body(world!, "row" + i, new BodyDesc { Type = Phys2d.BodyType.Static, Initial = new InitialState { X = i, Y = 0 } });
            Phys2d.Box(row!, "box", new BoxDesc { Hx = 0.2f, Hy = 0.2f });
        }
        var lone = Phys2d.Body(world!, "lone", new BodyDesc { Type = Phys2d.BodyType.Static, Initial = new InitialState { X = 1, Y = 5 } });
        Phys2d.Box(lone!, "box", new BoxDesc { Hx = 0.2f, Hy = 0.2f });
        Phys2d.Step(world!, 1.0f / 60.0f);

        var outer = Phys2d.RaycastAll(world!, Ray(0), h =>
        {
            innerCounts.Add(Phys2d.RaycastAll(world!, Ray(5), g => 1).Count);
            return 1;
        });
        Nested("2d raycast_all", outer.Count, 4, 1);

        var rowBox = new AabbDesc { MinX = 0.5f, MinY = -0.5f, MaxX = 4.5f, MaxY = 0.5f };
        var loneBox = new AabbDesc { MinX = 0.5f, MinY = 4.5f, MaxX = 1.5f, MaxY = 5.5f };
        int innerOverlaps = 0, innerShapes = 0;
        var overlaps = Phys2d.OverlapAabb(world!, rowBox, s =>
        {
            innerOverlaps += Phys2d.OverlapAabb(world!, loneBox, t => true).Count;
            innerShapes += Phys2d.BodyShapes(lone!).Count;
            return true;
        });
        Expect("2d overlap_aabb outer", overlaps.Count, 4);
        Expect("2d overlap_aabb inner", innerOverlaps, 4);
        Expect("2d body shapes inside overlap_aabb", innerShapes, 4);

        // RaycastAll と ShapeCastAll は結果の buffer を共有する。
        var o1 = Phys2d.RaycastAll(world!, Ray(0), h =>
        {
            innerCounts.Add(Phys2d.ShapeCastAll(world!, Cast(5), g => 1).Count);
            return 1;
        });
        Nested("2d raycast_all > shape_cast_all", o1.Count, 4, 1);
        var o2 = Phys2d.ShapeCastAll(world!, Cast(0), h =>
        {
            innerCounts.Add(Phys2d.RaycastAll(world!, Ray(5), g => 1).Count);
            return 1;
        });
        Nested("2d shape_cast_all > raycast_all", o2.Count, 4, 1);
        var o3 = Phys2d.ShapeCastAll(world!, Cast(0), h =>
        {
            innerCounts.Add(Phys2d.ShapeCastAll(world!, Cast(5), g => 1).Count);
            return 1;
        });
        Nested("2d shape_cast_all > shape_cast_all", o3.Count, 4, 1);

        // mover の芯が box に入ると plane の法線が決まらず拾われないので、box の上面に少し食い込む高さに置く。
        var rowMover = new MoverDesc { Ax = 0.5f, Ay = 0.35f, Bx = 4.5f, By = 0.35f, R = 0.2f };
        var loneMover = new MoverDesc { Ax = 0.8f, Ay = 5.35f, Bx = 1.2f, By = 5.35f, R = 0.2f };
        var o4 = Phys2d.CollideMover(world!, rowMover, p =>
        {
            innerCounts.Add(Phys2d.CollideMover(world!, loneMover, q => true).Count);
            return true;
        });
        Nested("2d collide_mover > collide_mover", o4.Count, 4, 1);
    }

    static RaycastDesc3d Ray3(float y)
    {
        return new RaycastDesc3d { X = 0, Y = y, Z = 0, Dx = 5, Dy = 0, Dz = 0 };
    }

    static ShapeProxyDesc3d SphereCast(float y)
    {
        return new ShapeProxyDesc3d { Sphere = new SphereProxy3d { R = 0.05f, Center = new Vec3d { X = 0, Y = y, Z = 0 } }, Dx = 5, Dy = 0, Dz = 0 };
    }

    static int EachRay3(float y)
    {
        return Phys3d.RaycastAll(w3!, Ray3(y), g => 1).Count;
    }

    static int EachCast3(float y)
    {
        return Phys3d.ShapeCastAll(w3!, SphereCast(y), g => 1).Count;
    }

    static int EachOverlapAabb3(AabbDesc3d box)
    {
        return Phys3d.OverlapAabb(w3!, box, g => true).Count;
    }

    static int EachOverlapShape3(ShapeProxyDesc3d shape)
    {
        return Phys3d.OverlapShape(w3!, shape, g => true).Count;
    }

    static WorldRef3d? w3;

    static void Check3d()
    {
        var world = Phys3d.World("nested3d", new WorldOpts3d { Gravity = new Vec3d { X = 0, Y = 0, Z = 0 } });
        w3 = world;
        Phys3d.Begin(world!);
        for (int i = 1; i <= 4; i++)
        {
            var row = Phys3d.Body(world!, "row" + i, new BodyDesc3d { Type = Phys3d.BodyType.Static, Initial = new InitialState3d { X = i, Y = 0, Z = 0 } });
            Phys3d.Box(row!, "box", new BoxDesc3d { Hx = 0.2f, Hy = 0.2f, Hz = 0.2f });
        }
        var lone = Phys3d.Body(world!, "lone", new BodyDesc3d { Type = Phys3d.BodyType.Static, Initial = new InitialState3d { X = 1, Y = 5, Z = 0 } });
        Phys3d.Box(lone!, "box", new BoxDesc3d { Hx = 0.2f, Hy = 0.2f, Hz = 0.2f });
        Phys3d.Step(world!, 1.0f / 60.0f);

        var outer = Phys3d.RaycastAll(world!, Ray3(0), h =>
        {
            innerCounts.Add(EachRay3(5));
            return 1;
        });
        Nested("3d raycast_all", outer.Count, 4, 1);

        // RaycastAll と ShapeCastAll は結果の buffer を共有する。
        var o1 = Phys3d.RaycastAll(world!, Ray3(0), h =>
        {
            innerCounts.Add(EachCast3(5));
            return 1;
        });
        Nested("3d raycast_all > shape_cast_all", o1.Count, 4, 1);
        var o2 = Phys3d.ShapeCastAll(world!, SphereCast(0), h =>
        {
            innerCounts.Add(EachRay3(5));
            return 1;
        });
        Nested("3d shape_cast_all > raycast_all", o2.Count, 4, 1);
        var o3 = Phys3d.ShapeCastAll(world!, SphereCast(0), h =>
        {
            innerCounts.Add(EachCast3(5));
            return 1;
        });
        Nested("3d shape_cast_all > shape_cast_all", o3.Count, 4, 1);

        // OverlapAabb、OverlapShape、body の shapes は結果の buffer を共有する。
        var rowBox = new AabbDesc3d { MinX = 0.5f, MinY = -0.5f, MinZ = -0.5f, MaxX = 4.5f, MaxY = 0.5f, MaxZ = 0.5f };
        var loneBox = new AabbDesc3d { MinX = 0.5f, MinY = 4.5f, MinZ = -0.5f, MaxX = 1.5f, MaxY = 5.5f, MaxZ = 0.5f };
        var rowShape = new ShapeProxyDesc3d { Box = new BoxProxy3d { Hx = 2, Hy = 0.5f, Hz = 0.5f, Center = new Vec3d { X = 2.5f, Y = 0, Z = 0 } } };
        var loneShape = new ShapeProxyDesc3d { Sphere = new SphereProxy3d { R = 0.5f, Center = new Vec3d { X = 1, Y = 5, Z = 0 } } };
        var a1 = Phys3d.OverlapAabb(world!, rowBox, s =>
        {
            innerCounts.Add(EachOverlapAabb3(loneBox));
            return true;
        });
        Nested("3d overlap_aabb > overlap_aabb", a1.Count, 4, 1);
        var a2 = Phys3d.OverlapAabb(world!, rowBox, s =>
        {
            innerCounts.Add(EachOverlapShape3(loneShape));
            return true;
        });
        Nested("3d overlap_aabb > overlap_shape", a2.Count, 4, 1);
        var a3 = Phys3d.OverlapShape(world!, rowShape, s =>
        {
            innerCounts.Add(EachOverlapAabb3(loneBox));
            return true;
        });
        Nested("3d overlap_shape > overlap_aabb", a3.Count, 4, 1);
        var a4 = Phys3d.OverlapShape(world!, rowShape, s =>
        {
            innerCounts.Add(EachOverlapShape3(loneShape));
            return true;
        });
        Nested("3d overlap_shape > overlap_shape", a4.Count, 4, 1);
        var a5 = Phys3d.OverlapShape(world!, rowShape, s =>
        {
            innerCounts.Add(Phys3d.BodyShapes(lone!).Count);
            return true;
        });
        Nested("3d overlap_shape > body shapes", a5.Count, 4, 1);

        var rowMover = new MoverDesc3d { A = new Vec3d { X = 0.5f, Y = 0.35f, Z = 0 }, B = new Vec3d { X = 4.5f, Y = 0.35f, Z = 0 }, R = 0.2f };
        var loneMover = new MoverDesc3d { A = new Vec3d { X = 0.8f, Y = 5.35f, Z = 0 }, B = new Vec3d { X = 1.2f, Y = 5.35f, Z = 0 }, R = 0.2f };
        var m = Phys3d.CollideMover(world!, rowMover, p =>
        {
            innerCounts.Add(Phys3d.CollideMover(w3!, loneMover, q => true).Count);
            return true;
        });
        Nested("3d collide_mover > collide_mover", m.Count, 4, 1);
    }

    public static void OnFrame(float dt)
    {
        Check2d();
        Check3d();
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
