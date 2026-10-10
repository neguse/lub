namespace TinyCs.Tcs2c.Tests;

// 言語機能 / runtime 表面ごとの 2 backend differential。各 program は
// Main の stdout を C (通常 + GC stress) と Lua で突き合わせる。
public partial class DifferentialTests
{
    [CFact]
    public void Enums_SwitchAndToString()
    {
        Backends.AssertParity("""
            using System;
            public enum Dir { North = 0, East = 1, South = 2, West = 3 }
            public class P
            {
                public static Dir Turn(Dir d) => d switch { Dir.North => Dir.East, Dir.East => Dir.South, Dir.South => Dir.West, _ => Dir.North };
                public static void Main()
                {
                    var d = Dir.North;
                    for (int i = 0; i < 5; i++) { d = Turn(d); Console.WriteLine(d.ToString()); }
                    Console.WriteLine((int)d);
                    Console.WriteLine(d == Dir.East);
                    var dict = new Dictionary<Dir, string>();
                    dict[Dir.South] = "s";
                    Console.WriteLine(dict.ContainsKey(Dir.South) + ":" + dict.ContainsKey(Dir.West));
                }
            }
            """.Replace("using System;", "using System;\nusing System.Collections.Generic;"), "P");
    }

    [CFact]
    public void Linq_WhereSelectOrderByAggregates()
    {
        Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            public class Item { public string Name; public int Price; public Item(string n, int p) { Name = n; Price = p; } }
            public class P
            {
                public static bool Cheap(Item i) => i.Price < 50;
                public static void Main()
                {
                    var items = new List<Item> { new Item("sword", 100), new Item("potion", 10), new Item("shield", 80), new Item("arrow", 2) };
                    var names = items.Where(i => i.Price >= 50).Select(i => i.Name).ToList();
                    Console.WriteLine(string.Join(",", names));
                    Console.WriteLine(items.Select(i => i.Price).Sum());
                    Console.WriteLine(items.Min(i => i.Price) + ":" + items.Max(i => i.Price));
                    Console.WriteLine(items.OrderBy(i => i.Price).First().Name);
                    Console.WriteLine(items.OrderByDescending(i => i.Name).First().Name);
                    Console.WriteLine(items.Any(i => i.Price > 90) + ":" + items.All(i => i.Price > 5));
                    Console.WriteLine(items.Count(i => i.Price > 5) + ":" + items.Count());
                    Console.WriteLine(items.Where(Cheap).Count());
                    Console.WriteLine(items.FirstOrDefault(i => i.Price > 1000) == null);
                    Console.WriteLine(items.Last().Name + ":" + items.LastOrDefault(i => i.Price > 50).Name);
                    var prices = items.Select(i => i.Price).OrderBy(p => p).Skip(1).Take(2).ToList();
                    Console.WriteLine(prices[0] + "," + prices[1]);
                    var byName = items.ToDictionary(i => i.Name, i => i.Price);
                    Console.WriteLine(byName["shield"]);
                    var nums = new List<int> { 5, 3, 9, 1 };
                    Console.WriteLine(nums.Contains(9) + ":" + nums.IndexOf(9) + ":" + nums.IndexOf(7));
                    nums.Sort();
                    Console.WriteLine(string.Join(" ", nums.Select(n => n.ToString())));
                    nums.Sort((a, b) => b - a);
                    Console.WriteLine(string.Join(" ", nums.Select(n => n.ToString())));
                    nums.Remove(9);
                    nums.RemoveAt(0);
                    Console.WriteLine(nums.Count + ":" + nums[0]);
                    nums.Clear();
                    Console.WriteLine(nums.Count);
                    var floats = new List<float> { 1.5f, 2.25f };
                    Console.WriteLine(floats.Sum() + ":" + floats.Max());
                }
            }
            """, "P");
    }

    [CFact]
    public void Strings_RuntimeSurface()
    {
        Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class P
            {
                public static void Main()
                {
                    var s = "  Hello, TinyC# World  ";
                    var t = s.Trim();
                    Console.WriteLine("[" + t + "]");
                    Console.WriteLine(t.Length);
                    Console.WriteLine(t.ToUpper() + "|" + t.ToLower());
                    Console.WriteLine(t.Contains("Tiny") + ":" + t.Contains("tiny"));
                    Console.WriteLine(t.IndexOf("o") + ":" + t.IndexOf("o", 5) + ":" + t.IndexOf("zzz"));
                    Console.WriteLine(t.StartsWith("Hello") + ":" + t.EndsWith("World") + ":" + t.EndsWith("x"));
                    Console.WriteLine(t.Replace("l", "L"));
                    Console.WriteLine(t.Substring(7) + "|" + t.Substring(0, 5));
                    var parts = "a,b,,c".Split(',');
                    Console.WriteLine(parts.Length + ":" + string.Join("/", parts));
                    var words = "one two  three".Split(' ');
                    Console.WriteLine(words.Length);
                    Console.WriteLine(string.Join("-", "x", "y", "z"));
                    Console.WriteLine(string.IsNullOrEmpty("") + ":" + string.IsNullOrEmpty("a"));
                    Console.WriteLine(t[0] + ":" + (int)t[1]);
                    var sum = 0;
                    foreach (var r in "héllo".EnumerateRunes()) sum += r.Value;
                    Console.WriteLine(sum);
                    var n = int.Parse("42") + (int)float.Parse("1.5");
                    Console.WriteLine(n);
                    var f = 3.14159f;
                    Console.WriteLine($"{f:F2}|{n,6}|{n,-4}|{n:D5}|{255:X}|{f:E2}");
                    Console.WriteLine($"{"pad",6}|{t.Length}|{(f > 3 ? "big" : "small")}");
                    var sb = "";
                    for (int i = 0; i < 3; i++) sb += i.ToString() + ";";
                    Console.WriteLine(sb);
                    Console.WriteLine("abc" == "ab" + "c");
                    Console.WriteLine(1.5f.ToString() + "|" + true.ToString() + "|" + 7.ToString());
                }
            }
            """, "P");
    }

    [CFact]
    public void Dictionaries_AllOperations()
    {
        Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            public class P
            {
                public static void Main()
                {
                    var d = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
                    d["c"] = 3;
                    d.Add("d", 4);
                    d["a"] = 10;
                    Console.WriteLine(d.Count + ":" + d["a"] + ":" + d.ContainsKey("zz"));
                    if (d.TryGetValue("b", out var b)) Console.WriteLine("b=" + b);
                    if (!d.TryGetValue("q", out var q)) Console.WriteLine("q=" + q);
                    Console.WriteLine(d.Remove("b") + ":" + d.Remove("b") + ":" + d.Count);
                    var total = 0;
                    foreach (var kv in d) total += kv.Value + kv.Key.Length;
                    Console.WriteLine(total);
                    var keys = new List<string>();
                    foreach (var k in d.Keys) keys.Add(k);
                    keys.Sort();
                    Console.WriteLine(string.Join(",", keys));
                    var values = new List<int>();
                    foreach (var v in d.Values) values.Add(v);
                    values.Sort();
                    Console.WriteLine(values.Sum() + ":" + values.Count);
                    var byId = new Dictionary<int, string>();
                    for (int i = 0; i < 100; i++) byId[i * 7] = "v" + i.ToString();
                    Console.WriteLine(byId.Count + ":" + byId[70] + ":" + byId.ContainsKey(71));
                    var hits = 0;
                    for (int i = 0; i < 700; i++) if (byId.ContainsKey(i)) hits++;
                    Console.WriteLine(hits);
                }
            }
            """, "P");
    }

    [CFact]
    public void Math_Facade()
    {
        Backends.AssertParity("""
            using System;
            public class P
            {
                public static void Main()
                {
                    Console.WriteLine(Math.Max(3, 7) + ":" + Math.Min(3, 7) + ":" + Math.Abs(-5) + ":" + Math.Sign(-2) + ":" + Math.Clamp(15, 0, 10));
                    Console.WriteLine(Math.Max(1.5f, 2f) + ":" + Math.Abs(-1.25f) + ":" + Math.Clamp(0.5f, 1f, 2f));
                    Console.WriteLine(Math.Floor(2.7f) + ":" + Math.Ceiling(2.1f) + ":" + Math.Round(2.5f) + ":" + Math.Round(3.5f) + ":" + Math.Round(2.345f, 2));
                    Console.WriteLine(Math.Sqrt(16f) + ":" + Math.Pow(2f, 10f) + ":" + Math.Exp(0f) + ":" + Math.Log(1f));
                    Console.WriteLine((int)(Math.Sin(0f) * 100f) + ":" + (int)(Math.Cos(0f) * 100f) + ":" + (int)(Math.Atan2(1f, 1f) * 1000f));
                    Console.WriteLine((int)(Math.PI * 1000f));
                    Console.WriteLine((int)2.7f + ":" + (int)-2.7f + ":" + (int)(7f / 2f));
                    Console.WriteLine(7 / 2 + ":" + -7 / 2 + ":" + 7 % 3 + ":" + -7 % 3 + ":" + 7.5f % 2f);
                    // 負数の >> は Lua が論理シフト (support-matrix の既知差異) なので正数のみ
                    Console.WriteLine((5 << 2) + ":" + (16 >> 2) + ":" + (6 & 3) + ":" + (6 | 3) + ":" + (6 ^ 3) + ":" + ~5);
                    int big = 2147483647;
                    Console.WriteLine(big + 1);
                    Console.WriteLine(1f / 3f);
                    Console.WriteLine(100000000f + 1f);
                }
            }
            """, "P");
    }

    [CFact]
    public void Math_HostReplacesLibmThroughTcsMath()
    {
        // TCS_MATH(name) を定義した host は、結果が IEEE-754 で決まらない数学関数を自分の実装にできる。
        // 差し替えた関数は自分の名前の番号を返す。
        // Pow(3, 2) は Lua の ^ と同じく乗算で、powf を呼ばない (powf(x, 2) は実装によって結果が subnormal のところで x * x と違う)。
        var c = Backends.EmitC(["""
            using System;
            public class P
            {
                public static void Main()
                {
                    Console.WriteLine(Math.Sin(0.5f) + ":" + Math.Cos(0.5f) + ":" + Math.Tan(0.5f) + ":" + Math.Exp(0.5f));
                    Console.WriteLine(Math.Atan2(1f, 2f) + ":" + Math.Pow(2f, 3f) + ":" + Math.Pow(3f, 2f));
                    Console.WriteLine(Math.Log(5f) + ":" + Math.Log(5f, 2f) + ":" + Math.Log(5f, 10f) + ":" + Math.Log(5f, 3f));
                    Console.WriteLine(Math.Sqrt(16f) + ":" + Math.Floor(2.5f));
                }
            }
            """], "P");
        string[] names = ["sinf", "cosf", "tanf", "expf", "atan2f", "powf", "logf", "log2f", "log10f"];
        var decls = string.Concat(names.Select(n => n == "atan2f" || n == "powf"
            ? $"float hooked_{n}(float, float);\n" : $"float hooked_{n}(float);\n"));
        var host = string.Concat(names.Select((n, i) => n == "atan2f" || n == "powf"
            ? $"float hooked_{n}(float a, float b) {{ (void)a; (void)b; return {i + 1}; }}\n"
            : $"float hooked_{n}(float a) {{ (void)a; return {i + 1}; }}\n"));
        var output = Backends.CompileAndRunC(
            "#define TCS_MATH(name) hooked_##name\n" + decls + c, extraCSource: host);
        Assert.Equal("1:2:3:4\n5:6:9\n7:8:9:1\n4:2", output);
    }

    [CFact]
    public void Classes_InheritanceVirtualAndTypeTests()
    {
        Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Shape
            {
                public string Name;
                public static int Created;
                public Shape(string name) { Name = name; Created = Created + 1; }
                public virtual float Area() => 0f;
                public virtual string Describe() => Name + ":" + Area().ToString();
            }
            public class Rect : Shape
            {
                public float W; public float H;
                public Rect(float w, float h) : base("rect") { W = w; H = h; }
                public override float Area() => W * H;
            }
            public class Square : Rect
            {
                public Square(float s) : base(s, s) { Name = "square"; }
                public override string Describe() => "sq:" + base.Describe();
            }
            public class Circle : Shape
            {
                public float R;
                public Circle(float r) : base("circle") { R = r; }
                public override float Area() => 3f * R * R;
            }
            public class P
            {
                public static void Main()
                {
                    var shapes = new List<Shape> { new Rect(2f, 3f), new Square(2f), new Circle(1f), new Shape("blob") };
                    foreach (var s in shapes) Console.WriteLine(s.Describe());
                    var rects = 0; var squares = 0;
                    foreach (var s in shapes)
                    {
                        if (s is Rect) rects++;
                        if (s is Square sq) { squares++; Console.WriteLine(sq.W); }
                        if (s is Circle c) Console.WriteLine("r=" + c.R);
                    }
                    Console.WriteLine(rects + ":" + squares + ":" + Shape.Created);
                    Shape nothing = null;
                    Console.WriteLine(nothing is Shape);
                    Console.WriteLine(nothing == null);
                }
            }
            """, "P");
    }

    [CFact]
    public void Closures_CaptureAndFuncLists()
    {
        Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Counter
            {
                public int N;
                public Func<int> Make(int step) { return () => { N = N + step; return N; }; }
            }
            public class P
            {
                public static int Twice(int x) => x * 2;
                public static void Main()
                {
                    var c = new Counter();
                    var inc = c.Make(3);
                    inc(); inc();
                    Console.WriteLine(c.N + ":" + inc());
                    var fs = new List<Func<int, int>>();
                    for (int i = 0; i < 3; i++) { var k = i; fs.Add(x => x + k); }
                    fs.Add(Twice);
                    var total = 0;
                    foreach (var f in fs) total += f(10);
                    Console.WriteLine(total);
                    Action<string> log = m => Console.WriteLine("log:" + m);
                    log("hi");
                    var acc = 0;
                    Action add = () => { acc += 5; };
                    add(); add();
                    Console.WriteLine(acc);
                    Func<int, int, int> op = (a, b) => a * b + acc;
                    Console.WriteLine(op(3, 4));
                }
            }
            """, "P");
    }

    [CFact]
    public void Structs_ValueSemanticsInArraysAndLists()
    {
        Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public struct Vec { public float X; public float Y; }
            public struct Body { public Vec Pos; public Vec Vel; public int Id; public string Tag; }
            public class P
            {
                public static Vec Add(Vec a, Vec b) { var r = new Vec(); r.X = a.X + b.X; r.Y = a.Y + b.Y; return r; }
                public static void Main()
                {
                    var bodies = new Body[3];
                    for (int i = 0; i < 3; i++)
                    {
                        var b = new Body();
                        b.Id = i; b.Tag = "b" + i.ToString();
                        b.Pos.X = i; b.Vel.Y = 1.5f;
                        bodies[i] = b;
                    }
                    for (int step = 0; step < 4; step++)
                        for (int i = 0; i < 3; i++)
                        {
                            var moved = Add(bodies[i].Pos, bodies[i].Vel);
                            bodies[i].Pos = moved;
                        }
                    var copy = bodies[2];
                    copy.Pos.X = 99f;
                    Console.WriteLine(bodies[2].Pos.X + ":" + bodies[2].Pos.Y + ":" + copy.Pos.X + ":" + bodies[2].Tag);
                    var list = new List<Vec>();
                    var v = new Vec(); v.X = 1f;
                    list.Add(v);
                    v.X = 2f;
                    list.Add(v);
                    Console.WriteLine(list[0].X + ":" + list[1].X + ":" + list.Count);
                }
            }
            """, "P");
    }

    [CFact]
    public void ControlFlow_LoopsAndStatics()
    {
        Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Config { public static int Limit = 10; public static string Label { get; set; } = "cfg"; public static List<int> Seen = new List<int>(); }
            public class P
            {
                public static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);
                public static void Main()
                {
                    var i = 0;
                    while (true) { i++; if (i % 2 == 0) continue; if (i > Config.Limit) break; Config.Seen.Add(i); }
                    Console.WriteLine(string.Join(",", Config.Seen.Select(x => x.ToString())));
                    var j = 0;
                    do { j += 3; } while (j < 10);
                    Console.WriteLine(j);
                    var pairs = "";
                    for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++) { if (b == a) continue; if (a + b > 3) break; pairs += a.ToString() + b.ToString() + " "; }
                    Console.WriteLine(pairs);
                    Config.Label = Config.Label + "!";
                    Console.WriteLine(Config.Label + ":" + Fib(15));
                    var grid = new List<List<int>>();
                    for (int r = 0; r < 3; r++) { var row = new List<int>(); for (int c = 0; c < 3; c++) row.Add(r * c); grid.Add(row); }
                    var sum = 0;
                    foreach (var row in grid) foreach (var cell in row) sum += cell;
                    Console.WriteLine(sum + ":" + grid[2][2]);
                    var sw = i switch { 11 => "eleven", 12 => "twelve", _ => "other" };
                    Console.WriteLine(sw);
                    string maybe = null;
                    Console.WriteLine((maybe ?? "fallback") + ":" + (maybe == null));
                    Console.WriteLine(Config.Seen.Count > 3 && Config.Seen[0] == 1 ? "yes" : "no");
                }
            }
            """.Replace("using System.Collections.Generic;", "using System.Collections.Generic;\nusing System.Linq;"), "P");
    }

    [CFact]
    public void DelegateFields_AndNumericTryParse()
    {
        Backends.AssertParity("""
            using System;
            public class Btn
            {
                public Action OnClick;
                public Func<int, int> Map { get; set; }
                public static Func<string> Describe;
                public void Fire() { OnClick(); }
                public string Name() { return Describe(); }
            }
            public class P
            {
                public static void Main()
                {
                    var b = new Btn();
                    var n = 0;
                    b.OnClick = () => { n++; };
                    b.Map = x => x * 2;
                    Btn.Describe = () => "btn";
                    b.Fire();
                    b.OnClick();
                    Console.WriteLine(n + ":" + b.Map(21) + ":" + Btn.Describe() + ":" + b.Name());
                    var s = 0;
                    if (int.TryParse("42", out var a)) s += a;
                    if (!int.TryParse("4x2", out var bad)) s += 1000 + bad;
                    int c;
                    var okc = int.TryParse(" 7 ", out c);
                    s += okc ? c : -1;
                    if (float.TryParse("1.5", out var f)) s += (int)(f * 2f);
                    if (!float.TryParse("abc", out var g)) s += 100 + (int)g;
                    Console.WriteLine(s);
                }
            }
            """, "P");
    }
}
