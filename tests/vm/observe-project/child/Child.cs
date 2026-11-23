// The app that ../game/Game.cs opens with App.Use. Increases Score by 3 every frame, and does some work that allocates a little and shows up in the per-line load.
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
