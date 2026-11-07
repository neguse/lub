// 行ごとの負荷 (Sys.ProfileLines、Sys.TakeProfile、Sys.TakeGcPauses、Sys.TakeKept、Sys.TakeGpuPasses) の検査。
// ゲームの OnFrame は毎 frame、Game.cs:17 で List を作って Game.Keep に置き、Game.cs:19 で足し算を繰り返し、Game.cs:20 で大きな配列を作って Game.Big に置く (数 frame で GC が走る)。
// その後、Game.cs:24 で MainTex への pass を始めて draw を 2 つ出す。
// 数え始めてから 4 frame の後に、その行に確保と時間が付き、GC の止まりが届き、その frame に作って static に置いた物が持ち越しとして届き、
// GPU が終えた frame の pass が、始めた行、描き先、draw の数、GPU の時間 (この test が走る vulkan では測れる) 付きで届き、止めた後は行の負荷と持ち越しが届かないことを見る。
using System;
using Lub;

public static class Tool
{
    static int frame;
    static bool done;

    static void Finish(string message, int code)
    {
        done = true;
        Console.WriteLine(message);
        App.Quit(code);
    }

    public static void OnFrame(float dt)
    {
        if (done)
            return;
        frame++;
        if (frame == 2)
        {
            Sys.ProfileLines(true);
            return;
        }
        if (frame == 6)
        {
            bool alloc = false;
            bool work = false;
            var sites = "";
            foreach (var p in Sys.TakeProfile())
            {
                sites += p.Site + " ";
                if (p.Site.EndsWith("Game.cs:17") && p.Allocs > 0 && p.Bytes > 0)
                    alloc = true;
                if (p.Site.EndsWith("Game.cs:19") && p.Ms > 0)
                    work = true;
            }
            if (!alloc || !work)
            {
                Finish("FAIL the profile did not have Game.cs:17 allocations and Game.cs:19 time: " + sites, 1);
                return;
            }
            var pauses = Sys.TakeGcPauses();
            if (pauses.Count == 0 || pauses[0].LiveKb <= 0)
            {
                Finish("FAIL no GC pause", 1);
                return;
            }
            bool keepList = false;
            bool keepBig = false;
            var kept = "";
            foreach (var k in Sys.TakeKept())
            {
                kept += k.Site + " " + k.Holder + " " + k.Count + " ";
                if (k.Site.EndsWith("Game.cs:17") && k.Holder == "Game.Keep" && k.Count > 0)
                    keepList = true;
                if (k.Site.EndsWith("Game.cs:20") && k.Holder == "Game.Big" && k.Count == 1 && k.Bytes >= 400000)
                    keepBig = true;
            }
            if (!keepList || !keepBig)
            {
                Finish("FAIL the kept objects were not Game.cs:17 in Game.Keep and Game.cs:20 in Game.Big: " + kept, 1);
                return;
            }
            bool timed = false;
            var gpu = "";
            foreach (var g in Sys.TakeGpuPasses())
            {
                gpu += g.Site + " " + g.Target + " " + g.Ms + "ms " + g.Draws + " draws, ";
                if (g.Site.EndsWith("Game.cs:24") && g.Target == "main" && g.Draws == 2 && g.Ms >= 0)
                    timed = true;
            }
            if (!timed)
            {
                Finish("FAIL no timed GPU pass begun at Game.cs:24 into main with 2 draws: " + gpu, 1);
                return;
            }
            Console.WriteLine("gpu passes: " + gpu);
            Sys.ProfileLines(false);
            return;
        }
        if (frame == 8)
        {
            if (Sys.TakeProfile().Count != 0 || Sys.TakeKept().Count != 0)
                Finish("FAIL the profile was counted after it stopped", 1);
            else
                Finish("PASS", 0);
        }
    }
}
