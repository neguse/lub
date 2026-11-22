// lider's drawing and search, called every frame, do not allocate (checked with the per-line allocations of Sys.ProfileLines).
//   - Search.Find does not create a string per declaration, even for a name that matches no declaration
//   - MonoText.Draw does not create a string per character, even when the density is not 1 (only for characters seen for the first time)
// Per-line load is attached only to apps opened from a .csproj with App.Use, so this entry does not run lider; it opens an app that calls lider's search and drawing (probe/TestAllocProbe.cs) and counts.
// The opened app also counts lider source lines, so allocations inside lider are attributed to lider lines, and allocations inside lub's cs-lib to the calling line.
// Checks that there are no allocations on the calling lines (FindLine, DrawLine) or on any lider line, and that there are allocations on the line that creates an array (ControlLine).
// Frames are counted from when the opened app starts running.
// Line numbers are positions in TestAllocProbe.cs, so if lines are added there, update ControlLine, FindLine, and DrawLine.
using System;
using Lub;

public static class TestAlloc
{
    const int ControlLine = 31;
    const int FindLine = 32;
    const int DrawLine = 37;
    static int frames;
    static bool done;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    public static void OnFrame(float dt)
    {
        if (done)
            return;
        var target = Gfx.UseTexture("test_alloc_target", 320, 180, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
        if (target == null)
            return;
        var probe = App.Use("test_alloc_probe", (Environment.GetEnvironmentVariable("LIDER_ROOT") ?? ".") + "/probe/TestAllocProbe.csproj");
        var ok = App.Step(probe, target, dt);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        // Step is false while waiting for the build
        if (!ok)
            return;
        frames++;
        if (frames == 3)
            Sys.ProfileLines(true, probe);
        if (frames < 8)
            return;
        int control = 0, find = 0, draw = 0, inside = 0;
        foreach (var p in Sys.TakeProfile(probe))
        {
            // lider source lines
            if (!p.Site.Contains("TestAllocProbe.cs:"))
                inside += p.Allocs;
            if (p.Site.EndsWith("TestAllocProbe.cs:" + ControlLine))
                control = p.Allocs;
            if (p.Site.EndsWith("TestAllocProbe.cs:" + FindLine))
                find = p.Allocs;
            if (p.Site.EndsWith("TestAllocProbe.cs:" + DrawLine))
                draw = p.Allocs;
        }
        Sys.ProfileLines(false, probe);
        var scanned = false;
        foreach (var n in Sys.Inspect("TestAllocProbe", 0, 100, probe))
            if (n.Name == "Scanned" && n.Text == "true")
                scanned = true;
        var got = "control " + control + ", Search.Find " + find + ", MonoText.Draw " + draw + ", inside lider " + inside + (scanned ? "" : " (no declarations scanned)");
        done = true;
        if (control == 0 || find != 0 || draw != 0 || inside != 0 || !scanned)
        {
            Console.WriteLine("FAIL allocations over 4 frames: " + got);
            App.Quit(1);
            return;
        }
        Console.WriteLine("PASS " + got);
        App.Quit(0);
    }
}
