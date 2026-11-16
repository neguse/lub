namespace TinyCs.Tcs2c.Tests;

/// <summary>
/// VM の状態の保存と読み込み (tvm_state_save / tvm_state_load、doc/vm-design.md §13.4)。
/// 状態を file に書き、別の process の VM に読み込んで続きを走らせ、止めずに走らせた結果と比べる。
/// </summary>
public class VmStateTests
{
    private const string Header = "using System;\nusing System.Collections.Generic;\nusing Random = TinySystem.Random;\n";

    // class、struct、record、配列、List、Dictionary (string と object の key)、closure、box、null、共有した参照、循環、Random
    private const string Types = """
        public struct Pt { public int X; public float Y; public bool On; }
        public struct Cell { public Pt At; public Enemy? Who; }
        public record Tag(string Name, int Level);
        public interface IShape { int Area(); }
        public class Box : IShape { public int W; public int H; public int Area() => W * H; }
        public class Enemy
        {
            public string Name = "";
            public int Hp;
            public int Armor;
            public Pt Pos;
            public Enemy? Target;
            public List<int> Log = new List<int>();
            public Enemy(string name, int hp) { Name = name; Hp = hp; }
        }
        """;

    private const string State = """
            static List<Enemy> enemies = new List<Enemy>();
            static Dictionary<string, int> scores = new Dictionary<string, int>();
            static Dictionary<Enemy, string> notes = new Dictionary<Enemy, string>();
            static Dictionary<int, Pt> marks = new Dictionary<int, Pt>();
            static Cell[] grid = new Cell[4];
            static List<Pt> path = new List<Pt>();
            static int[][] rows = new int[3][];
            static Func<int, int>? step;
            static Action? bump;
            static object? boxedInt;
            static object? boxedFloat;
            static object? boxedBool;
            static object? anyRef;
            static IShape? shape;
            static Tag? tag;
            static Pt origin;
            static Enemy? none;
            static string text = "";
            static float time;
            static int frame;
            static bool flag;
            static Random rng = new Random(7);
            static int[] empty = new int[0];
        """;

    private const string Setup = """
            public static void Setup()
            {
                var a = new Enemy("a", 10);
                var b = new Enemy("b", 20);
                a.Target = b;
                b.Target = a;
                enemies.Add(a);
                enemies.Add(b);
                enemies.Add(a);
                scores["a"] = 1;
                scores["gone"] = 9;
                scores["b"] = 2;
                scores.Remove("gone");
                notes[a] = "first";
                notes[b] = "second";
                marks[5] = new Pt { X = 5, Y = 0.5f, On = true };
                grid[1].Who = b;
                grid[2].At.X = 3;
                rows[0] = new int[] { 1, 2, 3 };
                rows[2] = new int[] { 4 };
                int k = 3;
                step = x => x + k;
                bump = () => { a.Hp += 100; };
                boxedInt = 42;
                boxedFloat = 1.5f;
                boxedBool = true;
                anyRef = b;
                shape = new Box { W = 2, H = 3 };
                tag = new Tag("t", 1);
                origin.X = -1;
                text = "héllo";
            }
        """;

    private const string Frame = """
            public static void Frame()
            {
                frame++;
                time += 0.25f;
                flag = !flag;
                foreach (var e in enemies) { e.Hp = step!(e.Hp); e.Pos.X += 1; e.Log.Add(frame); }
                bump!();
                scores["a"] += rng.Next(0, 100);
                scores["f" + frame] = frame;
                notes[enemies[0]] += "!";
                path.Add(new Pt { X = frame, Y = time, On = flag });
                marks[frame] = path[path.Count - 1];
                grid[frame % 4].At.Y += 1f;
                rows[1] = new int[] { frame };
                text += frame;
                tag = new Tag(tag!.Name, tag.Level + 1);
                origin.Y += 2f;
                int boxed = (int)boxedInt!;
                boxedInt = boxed + 1;
            }
        """;

    private const string Report = """
            public static void Report()
            {
                var a = enemies[0];
                var b = enemies[1];
                Console.WriteLine($"{frame} {time} {flag} {text} {origin.X} {origin.Y} {none == null} {empty.Length}");
                Console.WriteLine($"{a.Name}:{a.Hp}@{a.Pos.X} log={a.Log.Count} {b.Name}:{b.Hp}@{b.Pos.X} log={b.Log.Count}");
                Console.WriteLine($"shared={enemies[0] == enemies[2]} cycle={a.Target!.Target == a} any={anyRef == (object)b} grid={grid[1].Who == b}");
                foreach (var kv in scores) Console.Write($"{kv.Key}={kv.Value} ");
                Console.WriteLine();
                Console.WriteLine($"{notes[a]} {notes[b]} {notes.Count} {scores.ContainsKey("gone")}");
                foreach (var kv in marks) Console.Write($"{kv.Key}:{kv.Value.X},{kv.Value.Y},{kv.Value.On} ");
                Console.WriteLine();
                Console.WriteLine($"{grid[0].At.Y} {grid[1].At.Y} {grid[2].At.X} {grid[3].Who == null} {path.Count} {(path.Count > 0 ? path[0].Y : -1f)}");
                Console.WriteLine($"{rows[0]![2]} {(rows[1] == null ? -1 : rows[1]![0])} {rows[2]!.Length}");
                Console.WriteLine($"{step!(1)} {(int)boxedInt!} {(float)boxedFloat!} {(bool)boxedBool!} {shape!.Area()} {tag!.Name}{tag.Level} {rng.Next(0, 1000)}");
            }
        """;

    private static string Game(string types = Types, string state = State, string setup = Setup, string frame = Frame,
        string report = Report) =>
        Header + types + "\npublic static class Game\n{\n" + state + "\n" + setup + "\n" + frame + "\n" + report + "\n}\n";

    [Fact]
    public void LoadedState_ContinuesLikeTheRunThatWasNotStopped()
    {
        if (!Backends.VmEnabled) return;
        foreach (var saveAt in new[] { 0, 1, 5 })
        {
            var (baseline, code, stdout, stderr) = Backends.RunVmState([Game()], [Game()], "Game", saveAt, 8);
            Assert.True(code == 0, stderr);
            Assert.Equal(baseline, stdout);
        }
    }

    [Fact]
    public void LoadsIntoAVersionWithEditedBodiesAndOtherStatics()
    {
        if (!Backends.VmEnabled) return;
        // 関数の中身を変え、static を 1 つ足して 1 つ消した版にも読み込める。足した static は初期化子の値のまま
        const string v1 = Header + "public static class Game { static int n; static int gone = 3; public static void Setup() { n = 10; } public static void Frame() { n++; gone++; } public static void Report() { Console.WriteLine(n); } }";
        const string v2 = Header + "public static class Game { static int n; static int fresh = 7; public static void Setup() { n = 10; } public static void Frame() { n += 2; } public static void Report() { Console.WriteLine($\"{n} {fresh}\"); } }";
        var (_, code, stdout, stderr) = Backends.RunVmState([v1], [v2], "Game", 3, 5);
        Assert.True(code == 0, stderr);
        Assert.Equal("17 7", stdout);
    }

    // 形を変える編集に使う小さいゲーム。class、struct (field、static、List と Dictionary と配列の要素)、参照の field を持つ
    private const string Small = Header + """
        public struct Pt { public int X; public float Y; }
        public class Item { public int Id; }
        public class Enemy
        {
            public int Hp = 3;
            public int Armor = 5;
            public Pt Pos;
            public Item? Held;
            public List<Pt> Trail = new List<Pt>();
        }
        public static class Game
        {
            static List<Enemy> all = new List<Enemy>();
            static Pt origin;
            static Dictionary<int, Pt> marks = new Dictionary<int, Pt>();
            static Pt[] grid = new Pt[2];
            public static void Setup()
            {
                var e = new Enemy();
                e.Held = new Item { Id = 4 };
                all.Add(e);
                all.Add(new Enemy());
                origin.X = 7;
                origin.Y = 1.5f;
            }
            public static void Frame()
            {
                foreach (var e in all) { e.Hp++; e.Armor += 2; e.Pos.X += 1; e.Pos.Y += 0.5f; e.Trail.Add(e.Pos); }
                origin.X++;
                marks[all[0].Hp] = all[0].Pos;
                grid[1].X += 2;
                grid[1].Y += 0.25f;
            }
            public static void Report()
            {
                Console.WriteLine($"{all[0].Hp} {all[1].Hp}");
            }
        }
        """;

    private const string SmallReport = "Console.WriteLine($\"{all[0].Hp} {all[1].Hp}\");";

    /// <summary>形の違う版への読み込みは、reload (doc/object-model.md §6) と同じ決まりで値を移す。
    /// 同じ編集を、止めずに reload で当てた結果と、状態を保存して新版に読み込んだ結果が同じであること。
    /// edits は「旧版の文字列 => 新版の文字列」を `@@` で並べたもの、report は新版の Report の中身。</summary>
    [Theory]
    // class に initializer のある field を足す: 足した field は initializer の値
    [InlineData("public int Armor = 5;=>public int Armor = 5; public int Mana = 9;",
        "Console.WriteLine($\"{all[0].Hp} {all[0].Armor} {all[0].Mana} {all[1].Mana}\");", "5 9 9 9")]
    // class の field を消す: 残りの field は値を保つ
    [InlineData("public int Armor = 5;=>@@e.Armor += 2; =>",
        "Console.WriteLine($\"{all[0].Hp} {all[0].Pos.X} {all[0].Held!.Id} {all[0].Trail.Count}\");", "5 2 4 2")]
    // field の型を変える: 値は移さず、initializer の値になる
    [InlineData("public int Armor = 5;=>public float Armor = 5.5f;",
        "Console.WriteLine($\"{all[0].Hp} {all[0].Armor} {all[1].Armor}\");", "5 5.5 5.5")]
    // struct に field を足す: field、static、List と Dictionary と配列の要素のどれも、前の field は値を保ち、足した field は 0
    [InlineData("public struct Pt { public int X; public float Y; }=>public struct Pt { public int X; public float Y; public int Z; }",
        "Console.WriteLine($\"{all[0].Pos.X} {all[0].Pos.Y} {all[0].Pos.Z} {origin.X} {origin.Y} {origin.Z} {all[0].Trail[1].Y} {marks[5].X} {grid[1].Y} {grid[1].Z}\");",
        "2 1 0 9 1.5 0 1 2 0.5 0")]
    // struct の field の順を変える: 名前で対応する
    [InlineData("public struct Pt { public int X; public float Y; }=>public struct Pt { public float Y; public int X; }",
        "Console.WriteLine($\"{all[0].Pos.X} {all[0].Pos.Y} {origin.X} {origin.Y} {all[0].Trail[1].X} {marks[5].Y} {grid[1].X}\");", "2 1 9 1.5 2 1 4")]
    // 参照の field の型を変える: 値は移さない (null)
    [InlineData("public Item? Held;=>public object? Held;",
        "Console.WriteLine($\"{all[0].Hp} {all[0].Held == null} {all[1].Held == null}\");", "5 True True")]
    // object を作る initializer の field を足す: instance ごとに別の object
    [InlineData("public int Armor = 5;=>public int Armor = 5; public List<int> Log = new List<int>();",
        "Console.WriteLine($\"{all[0].Log.Count} {all[1].Log.Count} {all[0].Log != all[1].Log} {all[0].Armor}\");", "0 0 True 9")]
    public void ChangedShape_MovesLikeAReload(string edits, string report, string expected)
    {
        if (!Backends.VmEnabled) return;
        var v2 = Small;
        foreach (var edit in edits.Split("@@"))
        {
            var pair = edit.Split("=>");
            Assert.Contains(pair[0], v2);
            v2 = v2.Replace(pair[0], pair[1]);
        }
        Assert.Contains(SmallReport, v2);
        v2 = v2.Replace(SmallReport, report);
        var reloaded = Backends.RunVmReload([Small], [v2], "Game", 2);
        Assert.True(reloaded.ExitCode == 0, reloaded.Stderr);
        var (_, code, stdout, stderr) = Backends.RunVmState([Small], [v2], "Game", 2, 2);
        Assert.True(code == 0, stderr);
        Assert.Equal(reloaded.Stdout, stdout);
        Assert.Equal(expected, stdout);
    }

    [Fact]
    public void ChangedKindOfType_IsRefused()
    {
        if (!Backends.VmEnabled) return;
        const string v1 = Header + "public class C { public int A; } public static class Game { static List<C> all = new List<C>(); public static void Setup() { all.Add(new C()); } public static void Frame() { } public static void Report() { } }";
        const string v2 = Header + "public struct C { public int A; } public static class Game { static List<C> all = new List<C>(); public static void Setup() { all.Add(new C()); } public static void Frame() { } public static void Report() { } }";
        var (_, code, stdout, stderr) = Backends.RunVmState([v1], [v2], "Game", 1, 2);
        Assert.Equal(1, code);
        // class を struct にすると、それを要素に持つ容器の形も変わる
        Assert.Contains("tvm_state_load: type List<C> has a different element", stderr);
        Assert.Equal("", stdout);
    }

    [Fact]
    public void MissingTypeOrFunction_IsRefused()
    {
        if (!Backends.VmEnabled) return;
        const string v1 = Header + "public class Temp { public int N; } public static class Game { static object? o; static Func<int, int>? f; public static void Setup() { o = new Temp(); f = x => x + 1; } public static void Frame() { } public static void Report() { } }";
        const string noType = Header + "public static class Game { static object? o; static Func<int, int>? f; public static void Setup() { f = x => x + 1; } public static void Frame() { } public static void Report() { } }";
        const string noFunc = Header + "public class Temp { public int N; } public static class Game { static object? o; static Func<int, int>? f; public static void Setup() { o = new Temp(); } public static void Frame() { f = x => x + 1; } public static void Report() { } }";
        var first = Backends.RunVmState([v1], [noType], "Game", 1, 2);
        Assert.Equal(1, first.ExitCode);
        Assert.Contains("this version has no type Temp", first.Stderr);
        var second = Backends.RunVmState([v1], [noFunc], "Game", 1, 2);
        Assert.Equal(1, second.ExitCode);
        Assert.True(second.Stderr.Contains("this version has no function Game.Setup<lambda0:"), second.Stderr);
    }

    [Fact]
    public void RefusedLoad_LeavesTheStateAsItWas()
    {
        if (!Backends.VmEnabled) return;
        // 読み込めない状態 (型が今の版に無い) を読み込もうとしても、読み込む側の static は初期化子の値のまま
        const string v1 = Header + "public class C { public int A; } public static class Game { static object c = new C(); static int n = 1; public static void Setup() { n = 50; } public static void Frame() { } public static void Report() { } }";
        const string v2 = Header + "public static class Game { static object c = \"\"; static int n = 1; public static void Setup() { } public static void Frame() { } public static void Report() { } }";
        var (_, code, _, stderr) = Backends.RunVmState([v1], [v2], "Game", 1, 2);
        Assert.Equal(1, code);
        Assert.Contains("this version has no type C", stderr);
    }

    // 壊れた byte 列は断るだけで、VM は落ちない (終了コードは 0 か 1)
    [Fact]
    public void DamagedState_IsRefusedWithoutCrashing()
    {
        if (!Backends.VmEnabled) return;
        var rng = new Random(1234);
        for (var i = 0; i < 80; i++)
        {
            var mode = i % 3;
            var (_, code, _, stderr) = Backends.RunVmState([Game()], [Game()], "Game", 2, 3, bytes =>
            {
                if (mode == 0) return bytes[..rng.Next(0, bytes.Length)];
                var copy = (byte[])bytes.Clone();
                var flips = mode == 1 ? 1 : 8;
                for (var k = 0; k < flips; k++) copy[rng.Next(0, copy.Length)] = (byte)rng.Next(0, 256);
                return copy;
            });
            Assert.True(code is 0 or 1, $"case {i}: exit {code}\n{stderr}");
        }
    }
}
