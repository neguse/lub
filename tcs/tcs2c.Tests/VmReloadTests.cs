using TinyCs.Tests.SpecConformance;

namespace TinyCs.Tcs2c.Tests;

/// <summary>
/// VM の hot reload (tvm_apply、doc/object-model.md §6、doc/vm-design.md §12)。
/// T235 の reload fuzz の scenario を C# の driver で VM に当て、Lua の reload と同じ不変量
/// (retained の保持、added の initializer、identity、method の差し替え、static、OnReload 1 回) を確かめる。
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
        // v2 の Setup は走らない (状態は v1 が作る)。消えた field を参照しないよう空にする
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
        // Hp は型が変わった (int → float) ので initializer の値 (Lua の reload と同じ)
        Assert.Equal("""
            a:1.5/7@2,0,0 log=1
            b:1.5/7@2,0,0 log=1
            step 4 path 2 1,2,0
            marks 8,9 byId 42,1 home 5,6
            v2
            """, stdout);
    }

    [Fact]
    public void ChangedStaticInitializer_RequiresRestart()
    {
        if (!Backends.VmEnabled) return;
        const string v1 = Header + "public static class Game { static int n = 1; public static void Frame() { n++; } public static void Report() { Console.WriteLine(n); } }";
        const string v2 = Header + "public static class Game { static int n = 2; public static void Frame() { n++; } public static void Report() { Console.WriteLine(n); } }";
        var (code, _, stderr) = Reload(v1, v2);
        Assert.Equal(3, code);
        Assert.Contains("static initializer changed: Game.n", stderr);
    }

    [Fact]
    public void LiveClosureWithoutCounterpart_RequiresRestart()
    {
        if (!Backends.VmEnabled) return;
        const string v1 = Header + "public static class Game { static Func<int, int>? f; public static void Setup() { f = x => x + 1; } public static void Frame() { } public static void Report() { } }";
        const string v2 = Header + "public static class Game { static Func<int, int>? f; public static void Setup() { } public static void Frame() { } public static void Report() { Console.WriteLine(f == null); } }";
        var (code, stdout, stderr) = Reload(v1, v2);
        Assert.Equal(3, code);
        Assert.Contains("live closure runs Game.Setup<lambda0>", stderr);
        Assert.Equal("", stdout);
    }

    [Fact]
    public void AddedStatics_RunTheirInitializers_UnlessTheOwnerIsStillUninitialized()
    {
        if (!Backends.VmEnabled) return;
        // Cfg は遅延初期化 (initializer が定数でない)。Used は reload の前に初期化され、Unused はされない
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
}
