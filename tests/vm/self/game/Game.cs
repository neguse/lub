// Per-instance inspection interface (opens ../child/Child.cs with App.Use and inspects it).
//   - Sys.TakeTraceFor does not return lines that were only executed with this set to another instance of the same class as the pinned instance (Sys.InspectPin) (it does return lines of other classes and statics)
//   - Self of Gfx.PickResult is the Id of the this instance for each stack frame, in the same order as Site (an empty line for static frames)
//   - Sys.InspectInstances returns each instance reachable from statics once, with the same path and Id as Sys.Inspect
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
        // GAME_CS is <dir>/game/Game.cs
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
        // Step is false while waiting for the build
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
            // Draw was also executed by Left, so it is returned; Spin was executed only by Right, so it is not. The lines of the static OnFrame are returned
            var lines = Sys.TakeTraceFor("left", child);
            if (!Has(lines, "Child.cs:12") || Has(lines, "Child.cs:19") || !Has(lines, "Child.cs:46"))
                Fail("TakeTraceFor(left): " + string.Join(" ", lines));
        }
        if (frame == 5)
        {
            // Everything if key is empty
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
