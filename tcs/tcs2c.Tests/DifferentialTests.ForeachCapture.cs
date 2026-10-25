namespace TinyCs.Tcs2c.Tests;

// closure が foreach の反復変数を捕捉する。反復変数は反復ごとに別の変数
// (C# 5 以降)。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Closures_CaptureForeachVariablePerIteration()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            using System.Text;
            public struct Pt
            {
                public int X;
                public Pt(int x) { X = x; }
            }
            public class Holder
            {
                public List<Func<int>> Fs = new List<Func<int>>();
                public void Collect(List<int> xs)
                {
                    foreach (var v in xs) Fs.Add(() => v + xs.Count);
                }
            }
            public class P
            {
                static string Show(List<Func<int>> fs)
                {
                    var s = "";
                    foreach (var f in fs) s = s + f() + ",";
                    return s;
                }
                public static void Main()
                {
                    var xs = new List<int> { 1, 2, 3 };
                    var fs = new List<Func<int>>();
                    foreach (var v in xs) fs.Add(() => v);
                    Console.WriteLine(Show(fs));
                    fs = new List<Func<int>>();
                    foreach (var v in new int[] { 1, 2, 3 }) fs.Add(() => xs.Count(y => y < v));
                    Console.WriteLine(Show(fs));
                    fs = new List<Func<int>>();
                    foreach (var s in new List<string> { "a", "bb" })
                    {
                        Func<string> g = () => s + s;
                        fs.Add(() => g().Length);
                    }
                    foreach (char c in "hi") fs.Add(() => c);
                    foreach (var r in "ab".EnumerateRunes()) fs.Add(() => r.Value);
                    foreach (var p in new List<Pt> { new Pt(7), new Pt(8) }) fs.Add(() => p.X);
                    Console.WriteLine(Show(fs));
                    Func<List<int>, List<Func<int>>> inLambda = l =>
                    {
                        var r = new List<Func<int>>();
                        foreach (var v in l) r.Add(() => v * 10);
                        return r;
                    };
                    Console.WriteLine(Show(inLambda(xs)));
                    var h = new Holder();
                    h.Collect(xs);
                    Console.WriteLine(Show(h.Fs));
                    var cs = new List<int> { 1, 2, 2, 3 };
                    Func<int, int> eq = x => cs.Count(y => y == x);
                    Console.WriteLine(cs.Count(x => x > 1) + ":" + eq(2));
                }
            }
            """, "P");
        Assert.Equal("""
            1,2,3,
            0,1,2,
            2,4,104,105,97,98,7,8,
            10,20,30,
            4,5,6,
            3:2
            """.ReplaceLineEndings("\n"), output);
    }

    [CFact]
    public void Closures_CaptureForeachKeyValuePair()
    {
        // KeyValuePair は値: 反復の後に Dictionary を変えても捕捉した値は変わらない
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class D
            {
                public static void Main()
                {
                    var d = new Dictionary<string, int> { ["k"] = 9 };
                    var fs = new List<Func<string>>();
                    foreach (var kv in d) fs.Add(() => kv.Key + "=" + kv.Value);
                    d["k"] = 10;
                    d.Remove("k");
                    foreach (var f in fs) Console.WriteLine(f());
                }
            }
            """, "D");
        Assert.Equal("k=9", output);
    }

    [CFact]
    public void Closures_CaptureForeachVariableSurvivesFrames()
    {
        // 反復変数を捕捉した closure を static の List に残し、フレーム境界の
        // GC (stress 含む) をまたいで呼ぶ
        const string source = """
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public class Program
            {
                public static List<Func<string>> Kept = new List<Func<string>>();
                public static int Frames;
                public static void Setup()
                {
                    Kept = new List<Func<string>>();
                    Frames = 0;
                }
                public static void Frame()
                {
                    var tags = new List<string> { "a" + Frames, "b" + Frames };
                    foreach (var t in tags) Kept.Add(() => t + ":" + tags.Count(u => u != t));
                    var junk = new List<string>();
                    for (int i = 0; i < 20; i++) junk.Add("x" + i);
                    Frames = Frames + 1;
                }
                public static void Report()
                {
                    var all = "";
                    foreach (var f in Kept) all = all + f() + ",";
                    Console.WriteLine(all);
                }
            }
            """;
        Assert.Equal("a0:1,b0:1,a1:1,b1:1,a2:1,b2:1,a3:1,b3:1,",
            Backends.AssertParityLib(source, "Program", 4));
    }

    [CFact]
    public void Closures_BoxOnlyVariablesCapturedByInnerClosures()
    {
        // 捕捉 cell は、その関数の内側の closure が捕捉する変数にだけ作る。
        // 別の lambda が同じ名前の parameter / local を捕捉させていても、
        // `x => x > 1` の x と a の t は cell にしない (cs・eq の x・b の t の 3 つ)
        const string source = """
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public class P
            {
                public static void Main()
                {
                    var cs = new List<int> { 1, 2, 2, 3 };
                    Func<int, int> eq = x => cs.Count(y => y == x);
                    Func<int> a = () => { int t = 1; return t; };
                    Func<int> b = () => { int t = 2; return cs.Count(y => y == t); };
                    Console.WriteLine(cs.Count(x => x > 1) + ":" + eq(2) + ":" + a() + ":" + b());
                }
            }
            """;
        Assert.Equal("3:2:1:2", Backends.AssertParity(source, "P"));
        var c = Backends.EmitC([source], "P");
        Assert.Equal(3, c.Split("= tcs_new_cell(sizeof").Length - 1);
    }

    [CFact]
    public void Closures_LoopVariableShadowsCapturedOuterLocal()
    {
        // lambda の中のループ変数 / block の local が外の local と同じ名前でも、
        // その scope の外の参照は外の local を捕捉する。ループの入れ子と
        // break / continue の中の捕捉も見る
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Sh
            {
                public static void Main()
                {
                    var l = new List<int> { 1, 1 };
                    int v = 5;
                    Func<int> f1 = () => { var s = 0; foreach (var v in l) s += v; return s + v; };
                    Func<Func<int>> f2 = () => { var s = 0; foreach (var v in l) s += v; return () => s + v; };
                    int i = 9;
                    Func<int> f3 = () => { var s = 0; for (int i = 0; i < 2; i++) s += i; return s + i + 1; };
                    var kv = "changed";
                    var d = new Dictionary<string, int> { ["a"] = 1 };
                    Func<string> f4 = () => { var r = ""; foreach (var kv in d) r += kv.Key; return r + kv; };
                    int t = 5;
                    Func<bool, Func<int>> f5 = c => { if (c) { int t = 1; t++; } return () => t; };
                    Console.WriteLine(f1() + ":" + f2()() + ":" + f3() + ":" + f4() + ":" + f5(true)());
                    var fs = new List<Func<string>>();
                    foreach (var a in new List<int> { 1, 2, 3 })
                    {
                        if (a == 2) continue;
                        foreach (var b in new int[] { 10, 20, 30 })
                        {
                            if (b == 30) break;
                            fs.Add(() => a + ":" + b);
                        }
                    }
                    var all = "";
                    foreach (var f in fs) all = all + f() + ",";
                    Console.WriteLine(all);
                }
            }
            """, "Sh");
        Assert.Equal("""
            7:7:11:achanged:5
            1:10,1:20,3:10,3:20,
            """.ReplaceLineEndings("\n"), output);
    }

    [CFact]
    public void Closures_CapturedKeyValuePairSurvivesFrames()
    {
        // 捕捉した KeyValuePair の copy (string / List / struct の value) を
        // static に残し、後のフレームで元の Dictionary を上書き / Remove して
        // フレーム境界の GC (stress 含む) をまたいで読む。List は参照なので
        // 後の Add が見える
        const string source = """
            using System;
            using System.Collections.Generic;
            public struct Rec
            {
                public string Name;
                public int N;
                public Rec(string name, int n) { Name = name; N = n; }
            }
            public class Program
            {
                public static Dictionary<string, Func<string>> Kept = new Dictionary<string, Func<string>>();
                public static Dictionary<string, string> Ss = new Dictionary<string, string>();
                public static Dictionary<int, List<string>> Ls = new Dictionary<int, List<string>>();
                public static Dictionary<string, Rec> Rs = new Dictionary<string, Rec>();
                public static int Frames;
                public static void Setup()
                {
                    Kept = new Dictionary<string, Func<string>>();
                    Ss = new Dictionary<string, string>();
                    Ls = new Dictionary<int, List<string>>();
                    Rs = new Dictionary<string, Rec>();
                    Frames = 0;
                }
                public static void Frame()
                {
                    var f = Frames;
                    Ss["s" + f] = "v" + f;
                    Ls[f] = new List<string> { "l" + f };
                    Rs["r" + f] = new Rec("n" + f, f);
                    if (f == 1)
                    {
                        foreach (var kv in Ss) Kept["s" + kv.Key] = () => kv.Key + "=" + kv.Value;
                        foreach (var kv in Ls) Kept["l" + kv.Key] = () => kv.Key + "=" + kv.Value[0] + kv.Value.Count;
                        foreach (var kv in Rs) Kept["r" + kv.Key] = () => kv.Key + "=" + Show(kv.Value);
                    }
                    if (f == 2)
                    {
                        Ss["s0"] = "over";
                        Ss.Remove("s1");
                        Ls[0].Add("more");
                        Ls.Remove(1);
                        Rs["r0"] = new Rec("over", 99);
                        Rs.Remove("r1");
                    }
                    var junk = new List<string>();
                    for (int i = 0; i < 50; i++) junk.Add("x" + i + f);
                    Frames = f + 1;
                }
                static string Show(Rec r) => r.Name + r.N;
                public static void Report()
                {
                    var all = "";
                    foreach (var key in new List<string> { "ss0", "ss1", "l0", "l1", "rr0", "rr1" })
                        all = all + Kept[key]() + ",";
                    Console.WriteLine(all + Kept.Count);
                }
            }
            """;
        Assert.Equal("s0=v0,s1=v1,0=l02,1=l11,r0=n00,r1=n11,6",
            Backends.AssertParityLib(source, "Program", 5));
    }
}

