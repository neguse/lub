// Per-line cost (Lider.Costs, LoadProfile). The values counted by lub's Sys.ProfileLines are kept per line, per frame.
//   - Normally only 1 frame in 30 is counted, and the average over 4 samples is shown
//   - The line of the game (game/Game.cs) that repeats additions gets a time, the line that creates an array every frame and stores it in a static gets allocations and carry-over (the field that holds it is Game.keep), and the pass to MainTex started on line 18 appears with target main, 2 draws, and the GPU time (run with vulkan)
// Frames are counted from when the game starts running.
using System;
using Lub;
using LiderCore;

public static class TestProfile
{
    static int frames;

    public static void OnInit()
    {
        TestInit();
        Lider.OnInit();
    }

    public static void OnEvent(EventData e)
    {
        Lider.OnEvent(e);
    }

    public static void OnQuit()
    {
        Lider.OnQuit();
    }

    public static void OnFrame(float dt)
    {
        TestFrame(dt);
        Lider.OnFrame(dt);
    }

    static void TestInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    static void TestFrame(float dt)
    {
        // Wait for the game build
        if (GameApp.Frames == 0)
            return;
        frames++;
        LineCost? work = null;
        LineCost? alloc = null;
        PassCost? pass = null;
        foreach (var p in Lider.Costs.Passes)
            if (p.Path.EndsWith("Game.cs") && p.Line == 18 && p.Name == "main" && p.Draws == 2 && p.Gpu > 0)
                pass = p;
        foreach (var c in Lider.Costs.Lines)
        {
            if (c.Path.EndsWith("Game.cs") && c.Line == 13)
                work = c;
            if (c.Path.EndsWith("Game.cs") && c.Line == 14)
                alloc = c;
        }
        if (work != null && work.Ms > 0 && alloc != null && alloc.Bytes >= 4000 && alloc.Carry >= 1 && alloc.Holder == "Game.keep" && pass != null)
        {
            Console.WriteLine("PASS");
            App.Quit(0);
        }
        else if (frames > 400)
        {
            Console.WriteLine("FAIL no time on line 13, no allocation and kept object on line 14, or no timed pass begun at line 18 (" + Lider.Costs.Lines.Count + " lines" + (alloc != null ? ", line 14 kept " + alloc.Carry + " in '" + alloc.Holder + "'" : "") + ", " + Lider.Costs.Passes.Count + " passes)");
            App.Quit(1);
        }
    }
}
