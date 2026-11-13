// 個体ごとの検査の口 (../child/Child.cs を App.Use で開いて見る)。
//   - Sys.TakeTraceFor は、覚えた個体 (Sys.InspectPin) と同じ class の別の個体を this にして通っただけの行を返さない (別の class と static の行は返す)
//   - Gfx.PickResult の Self は、Site と同じ並びの段ごとの this の個体の Id (static の段は空の行)
//   - Sys.InspectInstances は static から辿れる個体を、Sys.Inspect と同じ path と Id で 1 度ずつ返す
using System;
using System.Collections.Generic;
using Lub;

public static class Game
{
    static int frame;
    static bool failed;
    static string left = "";
    static bool picked;

    static string ChildCsproj()
    {
        // GAME_CS は <dir>/game/Game.cs
        var game = Environment.GetEnvironmentVariable("GAME_CS") ?? "";
        return game.Replace("game/Game.cs", "child/Child.csproj");
    }

    static void Fail(string message)
    {
        if (failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }

    static bool Has(List<string> lines, string end)
    {
        foreach (var l in lines)
            if (l.EndsWith(end))
                return true;
        return false;
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        if (failed)
            return;
        var rt = Gfx.UseTexture("child", 64, 64, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
        if (rt == null) return;
        var child = App.Use("child", ChildCsproj());
        var ok = App.Step(child, rt, dt);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        if (Sys.Seconds() > 120)
        {
            Fail("timeout after " + frame + " frames of the opened app");
            return;
        }
        // build を待つ間は Step が false
        if (!ok) return;
        frame++;
        if (frame == 3)
        {
            foreach (var n in Sys.Inspect("Child", 0, 10, child))
                if (n.Name == "Left")
                    left = n.Id;
            var found = "";
            foreach (var i in Sys.InspectInstances(8, 1000, child))
                found += i.Type + " " + i.Path + (i.Id == left ? " (left)" : "") + ", ";
            if (found != "Crate Child.Left (left), Crate Child.Right, ")
            {
                Fail("InspectInstances: " + found);
                return;
            }
            Sys.InspectPin("left", "Child.Left", child);
            Sys.TakeTraceFor("left", child);
            Gfx.Pick(Gfx.MainTex!, 16, 32, child);
            return;
        }
        if (frame == 4)
        {
            // Draw は Left も通ったので返し、Spin は Right だけが通ったので返さない。static の OnFrame の行は返す
            var lines = Sys.TakeTraceFor("left", child);
            if (!Has(lines, "Child.cs:12") || Has(lines, "Child.cs:19") || !Has(lines, "Child.cs:46"))
                Fail("TakeTraceFor(left): " + string.Join(" ", lines));
        }
        if (frame == 5)
        {
            // key が空なら全部
            var all = Sys.TakeTraceFor("", child);
            if (!Has(all, "Child.cs:19"))
                Fail("TakeTraceFor(\"\") left out a line: " + string.Join(" ", all));
        }
        if (frame > 4 && !picked)
        {
            var hit = Gfx.PickResult(child);
            if (hit == null)
                return;
            picked = true;
            var sites = hit.Site.Split('\n');
            var selves = hit.Self.Split('\n');
            if (sites.Length != selves.Length || !sites[0].EndsWith("Child.cs:14") || selves[0] != left || selves[1] != "")
                Fail("pick: site '" + hit.Site + "' self '" + hit.Self + "' (left " + left + ")");
            else
            {
                Console.WriteLine("PASS");
                App.Quit(0);
            }
        }
    }
}
