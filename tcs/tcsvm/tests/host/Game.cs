// host 連携のテスト (host_test.c が stub.cs の面を登録して呼ぶ)。
using System;
using System.Collections.Generic;
using Eng;

public static class Game
{
    static List<string> seen = new List<string>();

    public static void OnInit()
    {
        Gfx.Begin(new Opts { W = 3, Title = "t" });
        Console.WriteLine(Gfx.Add(2, 3) + " " + Gfx.Name() + " " + Gfx.MainTex);
        Gfx.Load("a.txt", out var text, out var version);
        bool ok = Gfx.Try(7, out float y);
        Console.WriteLine(text + " " + version + " " + ok + " " + y);
        var tex = Gfx.Make(42);
        var many = Gfx.Many(3);
        var xs = Gfx.Range(4);
        xs.Add(10);
        var sq = Gfx.Squares(4);
        Console.WriteLine(Gfx.Handle(tex) + " v" + tex.Version + " " + Gfx.Sum(xs) + " " + many.Count + " " + many[2].W + many[2].Title
            + " " + sq.Length + " " + sq[3]);
        var ring = Gfx.Ring(2);
        Console.WriteLine(ring.Kind + " " + ring.R + " " + ring.Area());
        // (T)T?
        float? quad = ring.R * 4;
        Console.WriteLine((int)(float)quad);
    }

    // struct と out を C の layout の塊で host に渡す
    public static void Structs()
    {
        var a = new Vec2 { X = 1, Y = 2 };
        var b = new Vec2 { X = 3, Y = 4 };
        Console.WriteLine(Phys.Dot(a, b));
        var box = new Box2 { Min = a, Max = b, Solid = true, Layer = Layer.Front };
        var c = Phys.Center(box);
        Console.WriteLine(c.X + " " + c.Y);
        Phys.Size(box, out var size, out var solid);
        Console.WriteLine(size.X + " " + size.Y + " " + solid);
        bool found = Phys.Pick(7, out Hit hit);
        bool missing = Phys.Pick(-1, out Hit none);
        Console.WriteLine(found + " " + hit.Id + " " + hit.T + " " + missing + " " + none.Id + " " + none.T);
        Hit? near = Phys.Cast(a, 10);
        Hit? far = Phys.Cast(b, 1);
        Console.WriteLine(near.HasValue + " " + near.Value.Id + " " + near.Value.T + " " + far.HasValue);
        Console.WriteLine(Phys.Score(near, 1) + " " + Phys.Score(far, 1) + " " + Phys.Score(null, 2));
        // 中の Center の塊が外の Apply の塊を上書きすると、Apply が足す v が (4, 8) に化ける
        var moved = Phys.Apply(v => Phys.Center(new Box2 { Min = new Vec2 { X = v.X * 4, Y = v.Y * 4 } }), a);
        Console.WriteLine(moved.X + " " + moved.Y);
    }

    public static void OnEvent(Ev e)
    {
        seen.Add(e.Kind + ":" + e.X);
    }

    public static void OnFrame(float dt)
    {
        Console.WriteLine(Gfx.Len(3, 4) * dt + " " + string.Join(",", seen));
    }
}
