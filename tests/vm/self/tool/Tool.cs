// 個体ごとの検査の口 (tcs VM の player の overlay から)。
//   - Sys.TakeTraceFor は、覚えた個体 (Sys.InspectPin) と同じ class の別の個体を this にして通っただけの行を返さない (別の class と static の行は返す)
//   - Gfx.PickResult の Self は、Site と同じ並びの段ごとの this の個体の Id (static の段は空の行)
//   - Sys.InspectInstances は static から辿れる個体を、Sys.Inspect と同じ path と Id で 1 度ずつ返す
using System;
using System.Collections.Generic;
using Lub;

public static class Tool
{
    static int frame;
    static bool failed;
    static string left = "";
    static bool picked;

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

    public static void OnFrame(float dt)
    {
        if (failed)
            return;
        frame++;
        if (frame == 3)
        {
            foreach (var n in Sys.Inspect("Game", 0, 10))
                if (n.Name == "Left")
                    left = n.Id;
            var found = "";
            foreach (var i in Sys.InspectInstances(8, 1000))
                found += i.Type + " " + i.Path + (i.Id == left ? " (left)" : "") + ", ";
            if (found != "Crate Game.Left (left), Crate Game.Right, ")
            {
                Fail("InspectInstances: " + found);
                return;
            }
            Sys.InspectPin("left", "Game.Left");
            Sys.TakeTraceFor("left");
            Gfx.Size(out var w, out var h);
            Gfx.Pick(Gfx.MainTex!, w / 4, h / 2);
            return;
        }
        if (frame == 4)
        {
            // Draw は Left も通ったので返し、Spin は Right だけが通ったので返さない。static の OnFrame の行は返す
            var lines = Sys.TakeTraceFor("left");
            if (!Has(lines, "Game.cs:12") || Has(lines, "Game.cs:19") || !Has(lines, "Game.cs:46"))
                Fail("TakeTraceFor(left): " + string.Join(" ", lines));
        }
        if (frame == 5)
        {
            // key が空なら全部
            var all = Sys.TakeTraceFor("");
            if (!Has(all, "Game.cs:19"))
                Fail("TakeTraceFor(\"\") left out a line: " + string.Join(" ", all));
        }
        if (frame > 4 && !picked)
        {
            var hit = Gfx.PickResult();
            if (hit == null)
            {
                if (frame > 60)
                    Fail("no pick result");
                return;
            }
            picked = true;
            var sites = hit.Site.Split('\n');
            var selves = hit.Self.Split('\n');
            if (sites.Length != selves.Length || !sites[0].EndsWith("Game.cs:14") || selves[0] != left || selves[1] != "")
                Fail("pick: site '" + hit.Site + "' self '" + hit.Self + "' (left " + left + ")");
            else
            {
                Console.WriteLine("PASS");
                App.Quit(0);
            }
        }
    }
}
