// 行ごとの負荷 (Lider.Costs、LoadProfile)。lub の Sys.ProfileLines で数えた値を、行ごとに 1 frame あたりで持つ。
//   - 普段は 30 frame に 1 frame だけ数え、4 回分をならして出す
//   - ゲーム (game/Game.cs) の足し算を繰り返す行に時間が付き、毎 frame 配列を作って static に置く行に確保と持ち越し (持つ field は Game.keep) が付き、18 行目で始めた MainTex への pass が描き先 main、draw 2 つ、GPU の時間付きで出る (vulkan で走らせる)
// frame はゲームが動き始めてから数える。
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
        // ゲームの build を待つ
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
