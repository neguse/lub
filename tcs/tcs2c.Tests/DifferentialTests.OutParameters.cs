namespace TinyCs.Tcs2c.Tests;

// 自作メソッドの out (`out var` / `out _` だけ)。Lua は multi-return、C はポインタ。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void OutParameters_StaticCallPositions()
    {
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                static int calls;
                static bool Try(int a, out int r) { calls++; r = a * 2; return a > 0; }
                static void Two(out int a, out string b) { a = 1; b = "x"; }
                static void Early(bool stop, out int a) { a = 1; if (stop) return; a = 2; }
                static int Mid(out int a, int b) { a = b + 1; return b - 1; }
                static bool Body(out int a) => (a = 5) > 0;
                static void VoidBody(out int a) => a = 6;
                public static void Main()
                {
                    if (Try(3, out int v)) Console.WriteLine(v);
                    if (!Try(-1, out var w)) Console.WriteLine(w);
                    var ok = Try(4, out var x); Console.WriteLine(ok + "," + x);
                    Try(5, out var y); Console.WriteLine(y);
                    Try(6, out _); Console.WriteLine(calls);
                    Try(7, out int _); Console.WriteLine(calls);
                    Two(out var a, out var b); Console.WriteLine(a + b);
                    Two(out _, out var b2); Console.WriteLine(b2);
                    Early(true, out var e1); Early(false, out var e2); Console.WriteLine(e1 + "," + e2);
                    Console.WriteLine(Mid(out var m, 10) + "," + m);
                    Console.WriteLine(Body(out var b3) + "," + b3);
                    VoidBody(out var b4); Console.WriteLine(b4);
                    Console.WriteLine((Try(1, out var l) ? l : -1) + (Try(2, out var r) ? r : -1));
                    var n = 3;
                    while (Try(n, out var t)) { Console.WriteLine(t); n--; }
                    for (var i = 0; Try(2 - i, out var u); i++) Console.WriteLine(u);
                    Func<int, int> f = q => Try(q, out var o) ? o : 0;
                    Console.WriteLine(f(8));
                    Func<int> captured = () => 0;
                    if (Try(9, out var c)) captured = () => c + 1;
                    Console.WriteLine(captured());
                }
            }
            """, "P");
        Assert.Equal(
            "6\n-2\nTrue,8\n10\n5\n6\n1x\nx\n1,2\n9,11\nTrue,5\n6\n6\n6\n4\n2\n4\n2\n16\n19",
            output);
    }

    [CFact]
    public void OutParameters_ReceiversAndTypes()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public interface IShape { bool Area(int scale, out int area); }
            public class Sq : IShape
            {
                public int Side;
                public bool Area(int scale, out int area) { area = Side * Side * scale; return true; }
                public bool Self(out int s) { var ok = Area(1, out var a) && a > 0; s = a; return ok; }
            }
            public class Ci : IShape { public bool Area(int scale, out int area) { area = 3 * scale; return false; } }
            public struct V
            {
                public int X;
                public bool Get(out int x) { x = X; X = x + 1; return true; }
                public bool Twice(out int t) { var ok = Get(out var a); t = a * 2; return ok; }
            }
            public class Box { public string Tag = ""; }
            public class G<T> { public T Val; public bool Take(out T v) { v = Val; return v != null; } }
            public class P
            {
                static void Vals(out V v, out Box b, out int[] xs, out List<int> l, out float f, out bool t, out string s, out int? n)
                {
                    v = new V(); v.X = 4;
                    b = new Box(); b.Tag = "tag";
                    xs = new int[2]; xs[1] = 9;
                    l = new List<int>(); l.Add(7);
                    f = 1.5f; t = true; s = null; n = null;
                }
                static V Shared;
                static void Copied(out V v) { v = Shared; v.X = v.X + 1; }
                static bool Dflt<T>(out T v) { v = default; return true; }
                static void Fwd(out int a) { Two(out var b); a = b + 1; }
                static void Two(out int a) { a = 2; }
                public static void Main()
                {
                    var sq = new Sq(); sq.Side = 3;
                    IShape i = sq;
                    Console.WriteLine(i.Area(2, out var a1) + "," + a1);
                    i = new Ci();
                    Console.WriteLine(i.Area(2, out var a2) + "," + a2);
                    Console.WriteLine(sq.Self(out var s1) + "," + s1);
                    var st = new V(); st.X = 10;
                    st.Get(out var x1); st.Get(out var x2);
                    Console.WriteLine(x1 + "," + x2 + "," + st.X);
                    st.Twice(out var x3); Console.WriteLine(x3 + "," + st.X);
                    Vals(out var v, out var b, out var xs, out var l, out var f, out var t, out var s, out var n);
                    Console.WriteLine(v.X + b.Tag + xs[1] + l[0] + f + t + (s == null) + n.HasValue);
                    Shared.X = 1; Copied(out var c); c.X = 100; Console.WriteLine(Shared.X + "," + c.X);
                    var g = new G<string>(); g.Val = "gv";
                    Console.WriteLine(g.Take(out var gv) + gv);
                    var gi = new G<int>(); gi.Val = 3;
                    Console.WriteLine(gi.Take(out var gv2) + "," + gv2);
                    Dflt<int>(out var d1); Dflt<V>(out var d2); Dflt<string>(out var d3);
                    Console.WriteLine(d1 + "," + d2.X + "," + (d3 == null));
                    Fwd(out var fw); Console.WriteLine(fw);
                    var keep = new List<Box>();
                    for (var k = 0; k < 40; k++) { MakeBox(k, out var mb); keep.Add(mb); }
                    Console.WriteLine(keep[39].Tag + keep[0].Tag);
                }
                static void MakeBox(int k, out Box b) { var pad = new Box(); pad.Tag = "p" + k; b = new Box(); b.Tag = pad.Tag + "!"; }
            }
            """, "P");
        Assert.Equal(
            "True,18\nFalse,6\nTrue,9\n10,11,12\n24,13\n4tag97"
                + "1.5TrueTrueFalse\n1,100\nTruegv\nTrue,3\n0,0,True\n3\np39!p0!",
            output);
    }

    [CFact]
    public void OutParameters_EvaluationOrderOfArguments()
    {
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                static string log = "";
                static int L(string s, int v) { log += s; return v; }
                static P Recv() { log += "r"; return new P(); }
                int Sum(int a, out int r, int b) { log += "m"; r = a + b; return a - b; }
                public static void Main()
                {
                    var d = Recv().Sum(L("a", 5), out var r, L("b", 2));
                    Console.WriteLine(log + "," + d + "," + r);
                }
            }
            """, "P");
        Assert.Equal("rabm,3,7", output);
    }

    [CFact]
    public void OutParameters_LibFramesKeepObjectsAlive()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            public class Box { public string Tag = ""; }
            public class P
            {
                static List<Box> keep = new List<Box>();
                static Func<Box> last;
                static bool Make(int k, out Box b, out string s) { b = new Box(); b.Tag = "t" + k; s = b.Tag + "?"; return k % 2 == 0; }
                public static void Setup() { }
                public static void Frame()
                {
                    for (var k = 0; k < 30; k++)
                    {
                        if (Make(k, out var b, out var s)) keep.Add(b);
                        if (s == "never") last = () => b;
                    }
                }
                public static void Report() { Console.WriteLine(keep.Count + keep[keep.Count - 1].Tag); }
            }
            """;
        Backends.AssertParityLib(source, "P", 5);
    }
}
