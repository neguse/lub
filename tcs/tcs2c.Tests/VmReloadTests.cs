using TinyCs.Tests.SpecConformance;

namespace TinyCs.Tcs2c.Tests;

/// <summary>
/// VM hot reload (tvm_apply, docs/tcs/object-model.md §6, docs/tcs/vm-design.md §12).
/// Applies the T235 reload fuzz scenarios to the VM with a C# driver and checks the same invariants as the Lua reload
/// (retained values kept, added initializers, identity, method replacement, statics, OnReload once).
/// </summary>
public class VmReloadTests
{
    private static string Driver(FuzzReloadScenario s, string setup, string report) => $$"""
        using System;

        public static class Driver
        {
            static RC0? P1;
            static RC0? P2;
            static RC0? B1;
            static RC0? B2;
            static int failed;

            static void Ck(bool c, string what)
            {
                if (c) return;
                failed++;
                Console.WriteLine("FAIL " + what);
            }

            public static void Setup()
            {
        {{setup}}
            }

            public static void Frame() { }

            public static void Report()
            {
        {{report}}
                if (failed == 0) Console.WriteLine("ok");
            }
        }
        """;

    private static void AssertScenario(int seed)
    {
        var s = new FuzzReloadGenerator(seed).Generate();
        var v1 = Driver(s, s.StateCs, "Console.WriteLine(\"v1\");");
        // v2's Setup does not run (v1 creates the state). It is left empty so that it does not refer to removed fields
        var v2 = Driver(s, "", s.AssertsCs);
        var (code, stdout, stderr) = Backends.RunVmReload([s.V1, v1], [s.V2, v2], "Driver");
        Assert.True(code == 0 && stdout == "ok",
            $"seed {seed}: exit {code}\n{stdout}\n{stderr}\n--- v1 ---\n{s.V1}\n--- v2 ---\n{s.V2}\n--- asserts ---\n{s.AssertsCs}");
    }

    [Fact]
    public void FuzzScenarios_PassInvariants_ForFixedSeeds()
    {
        if (!Backends.VmEnabled) return;
        foreach (var seed in Enumerable.Range(0, 20))
            AssertScenario(seed);
    }

    // Sweep when TCS_FUZZ=1 (run-tests.sh and run-fuzz.sh). TCS_FUZZ_COUNT seeds (default 20) starting from TCS_FUZZ_SEED (default 1000)
    [Fact]
    public void FuzzReloadSweep_ScenariosSatisfyInvariants()
    {
        if (!Backends.VmEnabled || Environment.GetEnvironmentVariable("TCS_FUZZ") != "1") return;
        var baseSeed = int.TryParse(Environment.GetEnvironmentVariable("TCS_FUZZ_SEED"), out var s) ? s : 1000;
        var count = int.TryParse(Environment.GetEnvironmentVariable("TCS_FUZZ_COUNT"), out var n) ? n : 20;
        var failures = new List<string>();
        for (var seed = baseSeed; seed < baseSeed + count; seed++)
        {
            try
            {
                AssertScenario(seed);
            }
            catch (Exception e)
            {
                failures.Add(e.Message);
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count}/{count} seeds failed\n" + string.Join("\n====\n", failures));
    }

    private static (int Code, string Stdout, string Stderr) Reload(string v1, string v2, int frames = 1) =>
        Backends.RunVmReload([v1], [v2], "Game", frames);

    private const string Header = "using System;\nusing System.Collections.Generic;\n";

    [Fact]
    public void Relayout_MigratesClassesStructsInContainersClosuresAndStatics()
    {
        if (!Backends.VmEnabled) return;
        const string v1 = Header + """
            public struct Pt { public int X; public int Y; }
            public class Enemy
            {
                public int Hp;
                public string Name;
                public Pt Pos;
                public Enemy(string name, int hp) { Name = name; Hp = hp; }
            }
            public static class Game
            {
                static List<Enemy> enemies = new List<Enemy>();
                static List<Pt> path = new List<Pt>();
                static Pt[] marks = new Pt[2];
                static Dictionary<int, Pt> byId = new Dictionary<int, Pt>();
                static Pt home;
                static Func<int, int>? step;
                public static void Setup()
                {
                    enemies.Add(new Enemy("a", 10));
                    enemies.Add(new Enemy("b", 20));
                    int k = 3;
                    step = x => x + k;
                    home = new Pt { X = 5, Y = 6 };
                }
                public static void Frame()
                {
                    foreach (var e in enemies) { e.Hp = step!(e.Hp); e.Pos.X += 1; }
                    path.Add(new Pt { X = path.Count, Y = 2 });
                    marks[1] = new Pt { X = 8, Y = 9 };
                    byId[path.Count] = new Pt { X = 40 + path.Count, Y = 1 };
                }
                public static void Report() { }
            }
            """;
        const string v2 = Header + """
            public struct Pt { public float W; public int Y; public int X; }
            public class Enemy
            {
                public int Mana = 7;
                public float Hp = 1.5f;
                public string Name;
                public Pt Pos;
                public List<int> Log = new List<int>();
                public Enemy(string name, int hp) { Name = name; Hp = hp; }
                public string Show() => $"{Name}:{Hp}/{Mana}@{Pos.X},{Pos.Y},{Pos.W} log={Log.Count}";
                public void OnReload() { Log.Add(Mana); }
            }
            public static class Game
            {
                static List<Enemy> enemies = new List<Enemy>();
                static List<Pt> path = new List<Pt>();
                static Pt[] marks = new Pt[2];
                static Dictionary<int, Pt> byId = new Dictionary<int, Pt>();
                static Pt home;
                static Func<int, int>? step;
                static string banner = "v2";
                public static void Setup()
                {
                    enemies.Add(new Enemy("a", 10));
                    enemies.Add(new Enemy("b", 20));
                    int k = 3;
                    step = x => x + k;
                    home = new Pt { X = 5, Y = 6 };
                }
                public static void Frame() { }
                public static void Report()
                {
                    foreach (var e in enemies) Console.WriteLine(e.Show());
                    Console.WriteLine($"step {step!(1)} path {path.Count} {path[1].X},{path[1].Y},{path[1].W}");
                    Console.WriteLine($"marks {marks[1].X},{marks[1].Y} byId {byId[2].X},{byId[2].Y} home {home.X},{home.Y}");
                    Console.WriteLine(banner);
                }
            }
            """;
        var (code, stdout, stderr) = Reload(v1, v2, frames: 2);
        Assert.True(code == 0, stderr);
        // The type of Hp changed (int → float), so it gets the initializer's value (same as the Lua reload)
        Assert.Equal("""
            a:1.5/7@2,0,0 log=1
            b:1.5/7@2,0,0 log=1
            step 4 path 2 1,2,0
            marks 8,9 byId 42,1 home 5,6
            v2
            """, stdout);
    }

    [Fact]
    public void Session_ReturnsModuleThenBatchesAndKeepsTheLastGoodBuild()
    {
        if (!Backends.VmEnabled) return;
        // Incremental session (VmSession) used by the web worker and tcs2vm --watch
        var session = new TinyCs.Vm.VmSession(null, ["Game.*"], []);
        const string v1 = Header + "public static class Game { static int n; public static void Frame() { n++; } public static void Report() { Console.WriteLine(\"v1 \" + n); } }";
        const string bad = Header + "public static class Game { static int n; public static void Frame() { n++ } }";
        const string v2 = Header + "public static class Game { static int n; static int m = 5; public static void Frame() { n++; } public static void Report() { Console.WriteLine(\"v2 \" + n + \" \" + m); } }";
        const string v3 = Header + "public static class Game { static int n; static int m; public static void Frame() { n++; } public static void Report() { } }";
        var first = session.Update([v1]);
        Assert.Equal("module", first.Kind);
        Assert.Equal("error", session.Update([bad]).Kind);
        var second = session.Update([v2]);
        Assert.Equal("batch", second.Kind);
        var (code, stdout, stderr) = Backends.RunVmBatch(first.Module!, second.Batch!, "Game", 3);
        Assert.True(code == 0, stderr);
        Assert.Equal("v2 3 5", stdout);
        var third = session.Update([v3]);
        Assert.Equal("restart", third.Kind);
        Assert.Contains("static initializer removed: Game.m", third.Reasons!);
    }

    [Fact]
    public void Session_NotesAChangedInstanceFieldInitializer()
    {
        if (!Backends.VmEnabled) return;
        // Live instances keep their current values, so a change to the initializer of an instance field does not cause a restart but is reported to the host
        var session = new TinyCs.Vm.VmSession(null, ["Game.*"], []);
        const string v1 = Header + "public class Tree { public int Hp = 40; } public static class Game { static Tree t = new Tree(); public static void Frame() { } public static void Report() { Console.WriteLine(t.Hp + \" \" + new Tree().Hp); } }";
        const string v2 = Header + "public class Tree { public int Hp = 100; } public static class Game { static Tree t = new Tree(); public static void Frame() { } public static void Report() { Console.WriteLine(t.Hp + \" \" + new Tree().Hp); } }";
        var first = session.Update([v1]);
        var second = session.Update([v2]);
        Assert.Equal("batch", second.Kind);
        Assert.Equal(["instance field initializer changed: Tree.Hp"], second.Notes);
        var (code, stdout, stderr) = Backends.RunVmBatch(first.Module!, second.Batch!, "Game", 3);
        Assert.True(code == 0, stderr);
        Assert.Equal("40 100", stdout);
    }

    [Fact]
    public void ChangedStaticInitializer_ReevaluatesThatStaticAndItsReaders()
    {
        if (!Backends.VmEnabled) return;
        // Only statics and consts whose initializers changed, and statics that read them in their initializers, are re-evaluated. Other statics keep their current values
        const string report = "public static void Frame() { frames++; kept++; } public static void Report() { Console.WriteLine($\"{Speed} {Lives} {Twice} {Half} {frames} {kept} {list.Count}\"); } }";
        const string v1 = Header + "public static class Game { static float Speed = 3f; const int Lives = 3; static int Twice = Lives * 2; static float Half = Speed / 2f; static int frames; static int kept = 10; static List<int> list = new List<int> { 1, 2 }; " + report;
        const string v2 = Header + "public static class Game { static float Speed = 5f; const int Lives = 4; static int Twice = Lives * 2; static float Half = Speed / 2f; static int frames; static int kept = 10; static List<int> list = new List<int> { 1, 2 }; " + report;
        var (code, stdout, stderr) = Reload(v1, v2, 3);
        Assert.True(code == 0, stderr);
        Assert.Equal("5 4 8 2.5 3 13 2", stdout);
    }

    [Fact]
    public void ChangedStaticType_ReevaluatesThatStatic()
    {
        if (!Backends.VmEnabled) return;
        const string v1 = Header + "public static class Game { static int n = 1; static int m = 7; public static void Frame() { m++; } public static void Report() { } }";
        const string v2 = Header + "public static class Game { static float n = 1.5f; static int m = 7; public static void Frame() { m++; } public static void Report() { Console.WriteLine($\"{n} {m}\"); } }";
        var (code, stdout, stderr) = Reload(v1, v2);
        Assert.True(code == 0, stderr);
        Assert.Equal("1.5 8", stdout);
    }

    [Fact]
    public void RemovedStaticInitializer_RequiresRestart()
    {
        if (!Backends.VmEnabled) return;
        const string v1 = Header + "public static class Game { static int n = 1; public static void Frame() { n++; } public static void Report() { Console.WriteLine(n); } }";
        const string v2 = Header + "public static class Game { static int n; public static void Frame() { n++; } public static void Report() { Console.WriteLine(n); } }";
        var (code, _, stderr) = Reload(v1, v2);
        Assert.Equal(3, code);
        Assert.Contains("static initializer removed: Game.n", stderr);
    }

    [Fact]
    public void UnreachableObjectOfATypeTheNewVersionDrops_DoesNotBlockReload()
    {
        if (!Backends.VmEnabled) return;
        // v2 does not use Temp, so v2's module has no Temp type. The Temp that v1 discarded is unreachable, so the reload is not stopped
        const string v1 = Header + "public class Temp { public int N; } public static class Game { public static void Setup() { var t = new Temp(); t.N = 1; } public static void Frame() { } public static void Report() { } }";
        const string v2 = Header + "public class Temp { public int N; } public static class Game { public static void Setup() { } public static void Frame() { } public static void Report() { Console.WriteLine(\"ok\"); } }";
        var (code, stdout, stderr) = Reload(v1, v2);
        Assert.True(code == 0, stderr);
        Assert.Equal("ok", stdout);
    }

    [Fact]
    public void LiveClosureWithoutCounterpart_RequiresRestart()
    {
        if (!Backends.VmEnabled) return;
        const string v1 = Header + "public static class Game { static Func<int, int>? f; public static void Setup() { f = x => x + 1; } public static void Frame() { } public static void Report() { } }";
        const string v2 = Header + "public static class Game { static Func<int, int>? f; public static void Setup() { } public static void Frame() { } public static void Report() { Console.WriteLine(f == null); } }";
        var (code, stdout, stderr) = Reload(v1, v2);
        Assert.Equal(3, code);
        Assert.Contains("live closure runs Game.Setup<lambda0:", stderr);
        Assert.Equal("", stdout);
    }

    [Fact]
    public void LiveClosures_FollowTheirLambda_WhenLambdasAreInsertedOrEdited()
    {
        if (!Backends.VmEnabled) return;
        // Even if a lambda is added before them, lambdas are matched by body fingerprint, and lambdas whose bodies changed are matched by number if the count is the same (the new body takes effect)
        const string v1 = Header + """
            public static class Game
            {
                static Func<int, int>? f;
                static Func<int, int>? g;
                public static void Setup() { int k = 3; f = x => x + k; g = x => x * 2; }
                public static void Frame() { }
                public static void Report() { }
            }
            """;
        const string v2 = Header + """
            public static class Game
            {
                static Func<int, int>? f;
                static Func<int, int>? g;
                static Func<int, int>? h;
                public static void Setup() { h = x => x - 1; int k = 3; f = x => x + k; g = x => x * 2; }
                public static void Frame() { }
                public static void Report() { Console.WriteLine($"{f!(1)} {g!(5)} {h == null}"); }
            }
            """;
        var (code, stdout, stderr) = Reload(v1, v2);
        Assert.True(code == 0, stderr);
        Assert.Equal("4 10 True", stdout);

        const string v3 = Header + """
            public static class Game
            {
                static Func<int, int>? f;
                static Func<int, int>? g;
                public static void Setup() { int k = 3; f = x => x + k * 100; g = x => x * 2; }
                public static void Frame() { }
                public static void Report() { Console.WriteLine($"{f!(1)} {g!(5)}"); }
            }
            """;
        (code, stdout, stderr) = Reload(v1, v3);
        Assert.True(code == 0, stderr);
        Assert.Equal("301 10", stdout);
    }

    [Fact]
    public void AddedStatics_RunTheirInitializers_UnlessTheOwnerIsStillUninitialized()
    {
        if (!Backends.VmEnabled) return;
        // Cfg is lazily initialized (its initializer is not a constant). Used is initialized before the reload, and Unused is not
        const string v1 = Header + """
            public static class Used { public static List<int> Xs = new List<int>(); }
            public static class Unused { public static List<int> Ys = new List<int>(); }
            public static class Game
            {
                public static void Frame() { Used.Xs.Add(1); }
                public static void Report() { }
            }
            """;
        const string v2 = Header + """
            public static class Used { public static List<int> Xs = new List<int>(); public static int Extra = Xs.Count + 10; }
            public static class Unused { public static List<int> Ys = new List<int>(); public static int Extra = Ys.Count + 20; }
            public static class Game
            {
                public static void Frame() { }
                public static void Report() { Console.WriteLine($"{Used.Xs.Count} {Used.Extra} {Unused.Extra}"); }
            }
            """;
        var (code, stdout, stderr) = Reload(v1, v2);
        Assert.True(code == 0, stderr);
        Assert.Equal("1 11 20", stdout);
    }

    [Fact]
    public void HookFault_IsAWarning_AndTheReloadStays()
    {
        if (!Backends.VmEnabled) return;
        const string v1 = Header + """
            public class Box { public int V = 1; }
            public static class Game
            {
                static Box b = new Box();
                public static void Frame() { b.V = 5; }
                public static void Report() { }
            }
            """;
        const string v2 = Header + """
            public class Box
            {
                public int V = 1;
                public int W = 2;
                public void OnReload() { var xs = new List<int>(); W = xs[V]; }
            }
            public static class Game
            {
                static Box b = new Box();
                public static void Frame() { }
                public static void Report() { Console.WriteLine($"{b.V} {b.W}"); }
            }
            """;
        var (code, stdout, stderr) = Reload(v1, v2);
        Assert.True(code == 0, stderr);
        Assert.Equal("5 2", stdout);
        Assert.Contains("reload warning", stderr);
    }

    [Fact]
    public void Snapshot_RestoresStaticsByName_WhenReloadRenumbersThem()
    {
        if (!Backends.VmEnabled) return;
        const string v1 = Header + """
            public static class Game
            {
                static int A = 1;
                static List<int> L = new List<int>();
                public static void Frame() { A++; L.Add(A); }
                public static void Report() { }
            }
            """;
        // Adding a static with an initializer in front shifts the slots of the statics after it
        const string v2 = Header + """
            public static class Game
            {
                static List<int> Z = new List<int>();
                static int A = 1;
                static List<int> L = new List<int>();
                public static void Frame() { A++; L.Add(A); }
                public static void Report() { Console.WriteLine($"{A} {L.Count} {Z.Count}"); }
            }
            """;
        var (code, stdout, stderr) = Backends.RunVmReload([v1], [v2], "Game", 2, ["--snapshot", "1", "--restore", "2"]);
        Assert.True(code == 0, $"exit {code}\n{stderr}");
        Assert.Equal("2 1 0", stdout);
    }
}
