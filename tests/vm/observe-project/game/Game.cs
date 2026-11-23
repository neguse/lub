// The opener inspects an app opened from a .csproj: executed lines, assigned values, the static tree, per-line load, and per-pass GPU time.
// The source of the opened app is compiled with --trace.
// The source of the opener is not recorded.
using System;
using System.Collections.Generic;
using Lub;

public static class Game
{
    static int stepped;
    static bool sawLine, sawValue, sawProfile, sawPass;

    static string ChildCsproj()
    {
        // GAME_CS is <dir>/game/Game.cs
        var game = Environment.GetEnvironmentVariable("GAME_CS") ?? "";
        return game.Replace("game/Game.cs", "child/Child.csproj");
    }

    static void Fail(string message)
    {
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        var rt = Gfx.UseTexture("child", 4, 4, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
        if (rt == null) return;
        var child = App.Use("child", ChildCsproj());
        Sys.ProfileLines(true, child);
        var ok = App.Step(child, rt, dt);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        if (Sys.Seconds() > 120)
        {
            Fail($"line={sawLine} value={sawValue} profile={sawProfile} pass={sawPass} after {stepped} frames of the opened app");
            return;
        }
        // Step is false while waiting for the build
        if (!ok) return;
        stepped++;
        foreach (var l in Sys.TakeTrace(child))
        {
            if (l.Contains("Game.cs")) { Fail("the opening app was traced: " + l); return; }
            if (l.Contains("Child.cs:")) sawLine = true;
        }
        foreach (var v in Sys.TakeValues(child))
        {
            if (v.Site.EndsWith(":Score") && v.Text == (stepped * 3).ToString()) sawValue = true;
        }
        foreach (var p in Sys.TakeProfile(child))
        {
            if (p.Site.Contains("Child.cs:")) sawProfile = true;
        }
        foreach (var g in Sys.TakeGpuPasses(child))
        {
            if (g.Target == "main" && g.Site.Contains("Child.cs:")) sawPass = true;
        }
        // The opener's own passes are not counted
        if (Sys.TakeGpuPasses().Count != 0)
        {
            Fail("the opening app has GPU passes without ProfileLines");
            return;
        }
        var score = "";
        foreach (var n in Sys.Inspect("Child", 0, 10, child))
        {
            if (n.Name == "Score") score = n.Text;
        }
        if (score != (stepped * 3).ToString())
        {
            Fail($"frame {stepped}: Score of the opened app is {score}");
            return;
        }
        // The opener's own record is separate (the opener's source is not compiled with --trace, so it is empty)
        if (Sys.TakeTrace().Count != 0)
        {
            Fail("the opening app has its own trace");
            return;
        }
        if (sawLine && sawValue && sawProfile && sawPass)
        {
            Console.WriteLine("PASS");
            App.Quit(0);
        }
    }
}
