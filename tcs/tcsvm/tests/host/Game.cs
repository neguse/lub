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

    public static void OnEvent(Ev e)
    {
        seen.Add(e.Kind + ":" + e.X);
    }

    public static void OnFrame(float dt)
    {
        Console.WriteLine(Gfx.Len(3, 4) * dt + " " + string.Join(",", seen));
    }
}
