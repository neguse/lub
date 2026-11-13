// tcs VM の player の検査の口 (Sys.TakeTrace / TakeValues / Inspect / InspectPin) の検査。
// ../child/Child.cs を App.Use で開いて見る。
// 開いた app のソースは --trace で変換され、開いた側 (この file) は変換されない。
using System;
using System.Collections.Generic;
using Lub;

public static class Game
{
    static int frame;
    static bool failed;

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

    static string Names(List<InspectNode> nodes)
    {
        var s = "";
        foreach (var n in nodes)
            s += n.Name + "=" + n.Text + "(" + n.Count + ") ";
        return s;
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        if (failed)
            return;
        var rt = Gfx.UseTexture("child", 4, 4, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
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
        var lines = Sys.TakeTrace(child);
        var values = Sys.TakeValues(child);
        if (frame != 3)
            return;
        bool line = false;
        foreach (var l in lines)
            if (l.EndsWith("Child.cs:32"))
                line = true;
        if (!line)
            Fail("the trace did not have Child.cs:32: " + string.Join(" ", lines));
        bool score = false;
        bool local = false;
        foreach (var v in values)
        {
            if (v.Site.EndsWith(":9:Score") && v.Text == "3")
                score = true;
            if (v.Site.EndsWith(":doubled") && v.Text == "6")
                local = true;
        }
        if (!score || !local)
        {
            var all = "";
            foreach (var v in values)
                all += v.Site + "=" + v.Text + " ";
            Fail("the values were " + all);
        }
        foreach (var l in lines)
            if (l.Contains("Game.cs"))
                Fail("the opening app was traced: " + l);
        var roots = Names(Sys.Inspect("", 0, 10, child));
        if (!roots.StartsWith("Child="))
            Fail("roots: " + roots);
        var top = Names(Sys.Inspect("Child", 0, 10, child));
        if (!top.Contains("Score=3(0)") || !top.Contains("P=Player(3)") || !top.Contains("Counts={"))
            Fail("Child: " + top);
        var player = Names(Sys.Inspect("Child.P", 0, 10, child));
        if (player != "At=Pos{X=6 Y=0}(2) Hp=6(0) Items=[1](1) ")
            Fail("Child.P: " + player);
        var items = Names(Sys.Inspect("Child.P.Items", 0, 10, child));
        if (items != "[0]=7(0) ")
            Fail("Child.P.Items: " + items);
        var counts = Names(Sys.Inspect("Child.Counts", 0, 10, child));
        if (counts != "[\"a\"]=1(0) ")
            Fail("Child.Counts: " + counts);
        var bindings = Names(Sys.Inspect("Child.B", 0, 10, child));
        if (bindings != "blob=<native>(0) ")
            Fail("Child.B: " + bindings);
        var node = Sys.Inspect("Child", 1, 1, child)[0];
        Sys.InspectPin("p", node.Path, child);
        if (!Sys.InspectPinned("p", node.Id, child))
            Fail("the pinned player was not found");
        if (!failed)
        {
            Console.WriteLine("PASS");
            App.Quit(0);
        }
    }
}
