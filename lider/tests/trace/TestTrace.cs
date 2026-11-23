// Checks lider's record of executed lines (TraceLog).
//   - A "file:line" key is split at the last ':' (the file path may contain ':'), and keys that cannot be read are discarded
//   - Frame advances by 1 per Take, and Age is how many frames ago the line ran (-1 after Fade frames)
//   - Active is whether any part of the file ran within Fade frames
//   - The site "file:line:col:target" of an assigned value is split at the last 3 ':', and ValuesAt returns the values of a line in column order (they disappear after Fade frames).
//     ChangedAt is the frame where the text changed (a value seen for the first time does not count as changed), and Steady is the number of frames the text before the change lasted. For numbers, the values of the most recent Fade frames are kept in Samples
//   - Even if written differently, paths whose tails match at '/' boundaries are the same file, and files that come later are matched again
//   - If several files match, it chooses in the order of the same path, then the single longest match, and if it cannot choose, it picks none of them.
//     Workspace.Resolve, which looks up from the minimap list, chooses the same way
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestTrace
{
    static bool ran;
    static bool failed;

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
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
    }

    // Check only once on the first frame
    static void TestFrame(float dt)
    {
        if (ran)
            return;
        ran = true;
        CheckLog();
        CheckValues();
        CheckSameName();
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void CheckLog()
    {
        var log = new TraceLog();
        log.Take();
        Check(log.Frame == 1, "Frame is " + log.Frame + " after one Take, want 1");
        log.Hit("C:/src/a/Foo.cs:3");
        log.Hit("C:/src/a/Foo.cs:x");
        log.Hit(":4");
        log.Hit("Foo.cs");
        Check(log.Age("a/Foo.cs", 3) == 0, "line 3 of a/Foo.cs has age " + log.Age("a/Foo.cs", 3) + ", want 0");
        Check(log.Age("a/Foo.cs", 4) == -1, "line 4 of a/Foo.cs was never hit but has age " + log.Age("a/Foo.cs", 4));
        log.Hit("C:/src/xa/Baz.cs:1");
        Check(log.Age("a/Baz.cs", 1) == -1, "xa/Baz.cs was taken as a/Baz.cs");
        Check(log.Active("C:/src/a/Foo.cs"), "a/Foo.cs is not active after a hit");
        for (var i = 0; i < TraceLog.Fade - 1; i++)
            log.Take();
        Check(log.Frame == TraceLog.Fade, "Frame is " + log.Frame + ", want " + TraceLog.Fade);
        Check(log.Age("a/Foo.cs", 3) == TraceLog.Fade - 1, "line 3 has age " + log.Age("a/Foo.cs", 3) + ", want " + (TraceLog.Fade - 1));
        Check(log.Active("a/Foo.cs"), "a/Foo.cs is not active " + (TraceLog.Fade - 1) + " frames after a hit");
        log.Take();
        Check(log.Age("a/Foo.cs", 3) == -1, "line 3 has age " + log.Age("a/Foo.cs", 3) + " after fading, want -1");
        Check(!log.Active("a/Foo.cs"), "a/Foo.cs is still active after fading");

        // A path that could not be matched is matched again when its file comes
        Check(log.Age("/r/b/Bar.cs", 1) == -1, "b/Bar.cs has an age before any hit");
        log.Hit("b/Bar.cs:1");
        Check(log.Age("/r/b/Bar.cs", 1) == 0, "b/Bar.cs was not found after its first hit");
    }

    static void CheckValues()
    {
        var log = new TraceLog();
        log.Take();
        log.Value("C:/src/a/Foo.cs:3:20:b.Hp", "7");
        log.Value("C:/src/a/Foo.cs:3:9:a", "\"x:y\"");
        log.Value("C:/src/a/Foo.cs:x:9:a", "1");
        log.Value("Foo.cs:9", "1");
        var vs = log.ValuesAt("a/Foo.cs", 3);
        Check(vs.Count == 2 && vs[0].Label == "a" && vs[0].Col == 9 && vs[1].Label == "b.Hp" && vs[1].Text == "7",
            "line 3 has " + vs.Count + " values, want a then b.Hp");
        Check(vs.Count == 2 && vs[0].Text == "\"x:y\"", "the text of a was not kept as is");
        Check(log.ValuesAt("a/Foo.cs", 4).Count == 0, "line 4 has values");
        Check(vs.Count == 2 && vs[1].ChangedAt < 0, "the first value counted as a change");
        Check(vs.Count == 2 && vs[1].Count == 1 && vs[1].Samples[0] == 7, "the number 7 was not sampled");
        Check(vs.Count == 2 && vs[0].Count == 0, "a string was sampled");
        log.Take();
        log.Value("C:/src/a/Foo.cs:3:20:b.Hp", "7");
        Check(vs.Count == 2 && vs[1].ChangedAt < 0, "the same text counted as a change");
        log.Take();
        log.Value("C:/src/a/Foo.cs:3:20:b.Hp", "8.5");
        Check(vs.Count == 2 && vs[1].ChangedAt == log.Frame, "the change to 8.5 is at " + vs[1].ChangedAt + ", want " + log.Frame);
        Check(vs.Count == 2 && vs[1].Steady == 2, "7 lasted " + vs[1].Steady + " frames before the change, want 2");
        Check(vs.Count == 2 && vs[1].Count == 3 && vs[1].Samples[2] == 8.5f && vs[1].SampleAt[2] == log.Frame, "8.5 was not sampled after 7 and 7");
        // Values whose assignments stopped disappear after Fade frames. The last frame is frame 1 for a and frame 3 for b.Hp
        for (var i = 0; i < TraceLog.Fade - 2; i++)
            log.Take();
        Check(log.ValuesAt("a/Foo.cs", 3).Count == 1, "line 3 has " + log.ValuesAt("a/Foo.cs", 3).Count + " values after a faded, want 1");
        for (var i = 0; i < 2; i++)
            log.Take();
        Check(log.ValuesAt("a/Foo.cs", 3).Count == 0, "values of line 3 did not fade");
        // Numeric values are kept up to Fade, overwriting the oldest first
        for (var i = 0; i < TraceLog.Fade + 5; i++)
        {
            log.Take();
            log.Value("C:/src/a/Foo.cs:3:20:b.Hp", "" + i);
        }
        Check(vs[1].Count == TraceLog.Fade, "kept " + vs[1].Count + " samples, want " + TraceLog.Fade);
    }

    static void CheckSameName()
    {
        var log = new TraceLog();
        log.Hit("p/Same.cs:1");
        log.Hit("q/Same.cs:2");
        Check(log.Age("Same.cs", 1) == -1 && log.Age("Same.cs", 2) == -1, "Same.cs was taken as one of p/Same.cs and q/Same.cs");
        Check(log.Age("/r/p/Same.cs", 1) == 0, "/r/p/Same.cs was not taken as p/Same.cs");
        Check(TraceLog.Match("Same.cs", new List<string> { "p/Same.cs", "Same.cs" }) == "Same.cs", "Match did not prefer the same path");
        Check(TraceLog.Match("/r/src/Foo.cs", new List<string> { "Foo.cs", "src/Foo.cs" }) == "src/Foo.cs", "Match did not prefer the longest match");

        var w = Lider.Work;
        var root = Environment.GetEnvironmentVariable("LIDER_ROOT");
        Check(w != null && root != null, "LIDER_ROOT or the workspace is missing");
        if (failed)
            return;
        Io.SaveText(root + "/p/Same.cs", "// p\n");
        Io.SaveText(root + "/q/Same.cs", "// q\n");
        w!.Rescan();
        Check(w.Resolve("Same.cs") == null, "Resolve took Same.cs as " + w.Resolve("Same.cs"));
        Check(w.Resolve("p/Same.cs") == root + "/p/Same.cs", "Resolve took p/Same.cs as " + w.Resolve("p/Same.cs"));
    }

    // Report only the first failure and exit
    static void Check(bool cond, string message)
    {
        if (cond || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}
