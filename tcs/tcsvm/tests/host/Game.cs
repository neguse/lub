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
