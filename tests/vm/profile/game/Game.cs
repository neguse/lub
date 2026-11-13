// 行ごとの負荷 (Sys.ProfileLines、Sys.TakeProfile、Sys.TakeGcPauses、Sys.TakeKept、Sys.TakeGpuPasses) の検査。
// ../child/Child.cs を App.Use で開いて見る。
// 開いた app の OnFrame は毎 frame、Child.cs:17 で List を作って Child.Keep に置き、Child.cs:19 で足し算を繰り返し、Child.cs:20 で大きな配列を作って Child.Big に置く (数 frame で GC が走る)。
// その後、Child.cs:24 で MainTex への pass を始めて draw を 2 つ出す。
// 数え始めてから 4 frame の後に、その行に確保と時間が付き、GC の止まりが届き、その frame に作って static に置いた物が持ち越しとして届き、
// GPU が終えた frame の pass が、始めた行、描き先、draw の数、GPU の時間 (この test が走る vulkan では測れる) 付きで届き、止めた後は行の負荷と持ち越しが届かないことを見る。
using System;
using Lub;

public static class Game
{
    static int frame;
    static bool done;

    static string ChildCsproj()
    {
        // GAME_CS は <dir>/game/Game.cs
        var game = Environment.GetEnvironmentVariable("GAME_CS") ?? "";
        return game.Replace("game/Game.cs", "child/Child.csproj");
    }

    static void Finish(string message, int code)
    {
        done = true;
        Console.WriteLine(message);
        App.Quit(code);
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        if (done)
            return;
        var rt = Gfx.UseTexture("child", 64, 64, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
        if (rt == null) return;
        var child = App.Use("child", ChildCsproj());
        var ok = App.Step(child, rt, dt);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        if (Sys.Seconds() > 120)
        {
            Finish("FAIL timeout after " + frame + " frames of the opened app", 1);
            return;
        }
        // build を待つ間は Step が false
        if (!ok) return;
        frame++;
        if (frame == 2)
        {
            Sys.ProfileLines(true, child);
            return;
        }
        if (frame == 6)
        {
            bool alloc = false;
            bool work = false;
            var sites = "";
            foreach (var p in Sys.TakeProfile(child))
            {
                sites += p.Site + " ";
                if (p.Site.EndsWith("Child.cs:17") && p.Allocs > 0 && p.Bytes > 0)
                    alloc = true;
                if (p.Site.EndsWith("Child.cs:19") && p.Ms > 0)
                    work = true;
            }
            if (!alloc || !work)
            {
                Finish("FAIL the profile did not have Child.cs:17 allocations and Child.cs:19 time: " + sites, 1);
                return;
            }
            var pauses = Sys.TakeGcPauses(child);
            if (pauses.Count == 0 || pauses[0].LiveKb <= 0)
            {
                Finish("FAIL no GC pause", 1);
                return;
            }
            bool keepList = false;
            bool keepBig = false;
            var kept = "";
            foreach (var k in Sys.TakeKept(child))
            {
                kept += k.Site + " " + k.Holder + " " + k.Count + " ";
                if (k.Site.EndsWith("Child.cs:17") && k.Holder == "Child.Keep" && k.Count > 0)
                    keepList = true;
                if (k.Site.EndsWith("Child.cs:20") && k.Holder == "Child.Big" && k.Count == 1 && k.Bytes >= 400000)
                    keepBig = true;
            }
            if (!keepList || !keepBig)
            {
                Finish("FAIL the kept objects were not Child.cs:17 in Child.Keep and Child.cs:20 in Child.Big: " + kept, 1);
                return;
            }
            Sys.ProfileLines(false, child);
            return;
        }
        if (frame == 8)
        {
            // GPU の時間は、その frame を GPU が終えた後に届く
            bool timed = false;
            var gpu = "";
            foreach (var g in Sys.TakeGpuPasses(child))
            {
                gpu += g.Site + " " + g.Target + " " + g.Ms + "ms " + g.Draws + " draws, ";
                if (g.Site.EndsWith("Child.cs:24") && g.Target == "main" && g.Draws == 2 && g.Ms >= 0)
                    timed = true;
            }
            if (!timed)
            {
                Finish("FAIL no timed GPU pass begun at Child.cs:24 into main with 2 draws: " + gpu, 1);
                return;
            }
            Console.WriteLine("gpu passes: " + gpu);
            if (Sys.TakeProfile(child).Count != 0 || Sys.TakeKept(child).Count != 0)
                Finish("FAIL the profile was counted after it stopped", 1);
            else
                Finish("PASS", 0);
        }
    }
}
