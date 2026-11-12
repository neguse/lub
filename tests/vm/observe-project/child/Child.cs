// ../game/Game.cs が App.Use で開く app。frame ごとに Score を 3 ずつ増やし、少し確保して行ごとの負荷に出る仕事をする。
using System;
using System.Collections.Generic;
using Lub;

public static class Child
{
    static int Score;

    public static void OnFrame(float dt)
    {
        Score += 3;
        var work = new List<int>();
        for (var i = 0; i < 2000; i++) work.Add(i * Score);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
    }
}
