namespace TinyCs.Tcs2c.Tests;

// タプル: 戻り値、分解、入れ替え、保持、List の要素。Lua は分解するなら multi-return、保持するなら
// struct の table、C は struct。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Tuples_ReturnAndDeconstruct()
    {
        var output = Backends.AssertParity("""
            using System;
            public class C { public int F; public (int, string) Pair() { return (F, "c"); } }
            public struct V { public int X; public (int, int) Both() { return (X, X + 1); } }
            public interface I { (int, int) Get(); }
            public class A : I { public (int, int) Get() => (1, 2); }
            public class B : I { public (int, int) Get() { var t = (3, 4); t.Item1 = 30; return t; } }
            public class P
            {
                static int n;
                static (int, string) Two() { return (1, "a"); }
                static (int x, int y) Named(int a) => (a, a * 2);
                static (int, int) Fwd() { return Named(5); }
                static (int, int) Cond(bool c) { return c ? (1, 2) : (3, 4); }
                static (float, float) Conv() { return (1, 2); }
                static ((int, int), string) Nest() { return ((1, 2), "n"); }
                static (int, string) Null() { return (1, null); }
                static (int, int) Early(int a) { if (a > 0) return (a, 1); for (var i = 0; i < 3; i++) { if (i == 1) return (i, 2); } return (0, 3); }
                static int Next() { n++; return n; }
                static (T, T) Dup<T>(T v) { return (v, v); }
                public static void Main()
                {
                    var (a, b) = Two(); Console.WriteLine(a + b);
                    (int c, string d) = Two(); Console.WriteLine(c + d);
                    int e; string f; (e, f) = Two(); Console.WriteLine(e + f);
                    var (_, g) = Two(); Console.WriteLine(g);
                    var (h, _) = Named(3); Console.WriteLine(h);
                    var (i, j) = Fwd(); Console.WriteLine(i + "," + j);
                    var (k, l) = Cond(false); Console.WriteLine(k + "," + l);
                    var (m, o) = Conv(); Console.WriteLine(m / 2 + "," + o / 4);
                    var ((p, q), r) = Nest(); Console.WriteLine(p + "," + q + r);
                    var (s, u) = (Next(), Next()); Console.WriteLine(s + "," + u);
                    var (w, x) = Null(); Console.WriteLine(w + "," + (x == null));
                    Console.WriteLine(Early(5).Item2 + "," + Early(0).Item1 + Early(0).Item2);
                    var o1 = new C(); o1.F = 9; var (y, z) = o1.Pair(); Console.WriteLine(y + z);
                    var st = new V(); st.X = 7; var (aa, ab) = st.Both(); Console.WriteLine(aa + "," + ab);
                    I it = new A(); var (ac, ad) = it.Get(); Console.WriteLine(ac + "," + ad);
                    it = new B(); var (ae, af) = it.Get(); Console.WriteLine(ae + "," + af);
                    var (ag, ah) = Dup("v"); var (ai, aj) = Dup(2); Console.WriteLine(ag + ah + ai + aj);
                    Two();
                    Console.WriteLine(n);
                }
            }
            """, "P");
        Assert.Equal(
            "1a\n1a\n1a\na\n3\n5,10\n3,4\n0.5,0.5\n1,2n\n1,2\n1,True\n1,12\n9c\n7,8\n1,2\n30,4\nvv22\n2",
            output);
    }

    [CFact]
    public void Tuples_SwapAndTargets()
    {
        var output = Backends.AssertParity("""
            using System;
            public class H { public int F; public int G; public int[] A = new int[2]; }
            public struct S { public int N; }
            public class P
            {
                static string log = "";
                static int L(string s, int v) { log += s; return v; }
                static H h = new H();
                static H GetH(string s) { log += s; return h; }
                static int St;
                public static void Main()
                {
                    int a = 1, b = 2;
                    (a, b) = (b, a); Console.WriteLine(a + "," + b);
                    var arr = new int[] { 1, 2, 3 };
                    (arr[0], arr[2]) = (arr[2], arr[0]); Console.WriteLine(arr[0] + "," + arr[2]);
                    (h.F, h.G) = (10, 20); (h.F, h.G) = (h.G, h.F); Console.WriteLine(h.F + "," + h.G);
                    (arr[0], arr[0]) = (7, 8); Console.WriteLine(arr[0]);
                    var i = 0;
                    (i, arr[i]) = (2, 50); Console.WriteLine(i + "," + arr[0] + "," + arr[2]);
                    (GetH("l").F, arr[L("i", 1)]) = (L("x", 1), L("y", 2)); Console.WriteLine(log);
                    (St, b) = (5, St); Console.WriteLine(St + "," + b);
                    (_, a) = (L("d", 1), 9); Console.WriteLine(a + log);
                    float f; int n; (f, n) = (1, 2); Console.WriteLine(f / 2 + "," + n);
                    var s1 = new S(); s1.N = 1; var s2 = new S(); s2.N = 2;
                    (s1, s2) = (s2, s1); s1.N = 20; Console.WriteLine(s1.N + "," + s2.N);
                    var t = (s1, 1); t.Item1.N = 21; Console.WriteLine(s1.N + "," + t.Item1.N);
                    (var c1, a) = (1, 2); Console.WriteLine(c1 + a);
                    int x1, y1, z1; ((x1, y1), z1) = ((1, 2), 3); Console.WriteLine(x1 + y1 + z1);
                    var v = (1, 2); (a, b) = v; Console.WriteLine(a + b);
                }
            }
            """, "P");
        Assert.Equal(
            "2,1\n3,1\n20,10\n8\n2,50,1\nlixy\n5,0\n9lixyd\n0.5,2\n20,1\n20,21\n3\n6\n3",
            output);
    }

    [CFact]
    public void Tuples_HeldAsValues()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public struct S { public int N; }
            public class Holder
            {
                public (int x, string y) Field = (1, "f");
                public (int, int) Zero;
                public (int, int) Prop { get; set; }
                public (S, int) WithS;
                public static (int, int) Stat = (8, 9);
            }
            public class G<T> { public (T, int) Pair; public (T, int) Make(T v) { return (v, 1); } }
            public class P
            {
                static (int, int) Two(int a) { return (a, a + 1); }
                static int Sum((int a, int b) t) { t.a = t.a + 1; return t.a + t.b; }
                static (int, int) Group() => Two(1);
                public static void Main()
                {
                    var t = (1, "x"); Console.WriteLine(t.Item1 + t.Item2);
                    (int x, string y) n = (2, "y"); Console.WriteLine(n.x + n.y + n.Item1);
                    var inferred = (a: 3, b: 4); Console.WriteLine(inferred.a + inferred.b);
                    var c = t; c.Item1 = 5; Console.WriteLine(t.Item1 + "," + c.Item1);
                    var h = new Holder(); Console.WriteLine(h.Field.x + h.Field.y + h.Zero.Item2 + Holder.Stat.Item2);
                    h.Field.x = 6; h.Field = (h.Field.x + 1, "g"); Console.WriteLine(h.Field.x + h.Field.y);
                    h.Prop = (1, 2); var pr = h.Prop; pr.Item1 = 9; Console.WriteLine(h.Prop.Item1 + "," + pr.Item1);
                    var s = new S(); s.N = 1; h.WithS = (s, 1); s.N = 2; Console.WriteLine(h.WithS.Item1.N);
                    var held = Two(3); Console.WriteLine(held.Item1 + held.Item2);
                    Console.WriteLine(Two(4).Item2);
                    Console.WriteLine(Sum(held) + "," + held.Item1);
                    Console.WriteLine(Sum(Two(5)) + "," + Sum((1, 1)));
                    var dflt = default((int, string)); Console.WriteLine(dflt.Item1 + "," + (dflt.Item2 == null));
                    var list = new List<(int, int)>(); list.Add((1, 2)); list.Add(Two(5));
                    var lit = new List<(int, int)> { (7, 8) }; list.Add(lit[0]);
                    var e = list[0]; e.Item1 = 100; Console.WriteLine(list[0].Item1 + "," + list[1].Item2 + "," + list.Count);
                    foreach (var (la, lb) in list) Console.Write(la + lb + ";");
                    foreach ((int la, int lb) in list) Console.Write(la * lb + ";");
                    foreach (var it in list) Console.Write(it.Item1 + ";");
                    Console.WriteLine();
                    Console.WriteLine(list.Contains((1, 2)) + "," + list.Contains((1, 3)) + "," + list.IndexOf((6, 5)));
                    var mapped = list.Select(v => (v.Item2, v.Item1)); Console.WriteLine(mapped[0].Item1);
                    Func<int, (int, int)> lam = v => (v, v * 2); var (l1, l2) = lam(4); Console.WriteLine(l1 + l2); Console.WriteLine(lam(1).Item2);
                    Func<int, (int, int)> grp = Two; var gv = grp(1); Console.WriteLine(gv.Item2);
                    Func<(int, int)> grp2 = Group; Console.WriteLine(grp2().Item1);
                    var arr = new (int, string)[2]; arr[1] = (1, "z"); Console.WriteLine(arr[1].Item2 + arr[0].Item1);
                    arr[0].Item1 = 4; Console.WriteLine(arr[0].Item1);
                    var d = new Dictionary<string, (int, int)>(); d["k"] = (1, 2); Console.WriteLine(d["k"].Item2);
                    (int, int)? opt = (1, 2); Console.WriteLine(opt.HasValue + "," + opt.Value.Item2);
                    (int, int)? none = null; Console.WriteLine(none.HasValue);
                    var nest = ((1, 2), (3, "s")); nest.Item2.Item1 = 4; var cp = nest; cp.Item1.Item1 = 9; Console.WriteLine(nest.Item1.Item1 + "," + nest.Item2.Item1 + cp.Item2.Item2);
                    var g = new G<string>(); g.Pair = g.Make("gs"); Console.WriteLine(g.Pair.Item1 + g.Pair.Item2);
                    var gi = new G<S>(); var mk = gi.Make(s); s.N = 77; Console.WriteLine(mk.Item1.N); Console.WriteLine(gi.Pair.Item1.N);
                    var big = (1, 2, 3, 4, 5, 6, 7); Console.WriteLine(big.Item7 + big.Item3);
                    var three = (1, 2.5f, true); Console.WriteLine(three.Item1 + "," + three.Item2 + "," + three.Item3);
                    var (ta, tb, tc) = three; Console.WriteLine(ta + "," + tb + "," + tc);
                }
            }
            """, "P");
        Assert.Equal(
            "1x\n2y2\n7\n1,5\n1f09\n7g\n1,9\n1\n7\n5\n8,3\n12,3\n0,True\n1,6,3\n3;11;15;2;30;56;1;5;7;\n"
                + "True,False,-1\n2\n12\n2\n2\n1\nz0\n4\n2\nTrue,2\nFalse\n1,4s\ngs1\n2\n0\n10\n1,2.5,True\n1,2.5,True",
            output);
    }

    [CFact]
    public void Tuples_GenericDispatchAndKeys()
    {
        var output = Backends.AssertParity("""
            using System;
            public interface IGet<T> { T Get(); }
            public class Direct : IGet<(int, int)> { public (int, int) Get() { return (1, 2); } }
            public class Wrap<T> : IGet<T> { public T V; public T Get() { return V; } }
            public static class Ext { public static (int, int) Twice(this int v) { return (v, v * 2); } }
            public struct S { public int N; }
            public class P
            {
                static T Id<T>(T v) { return v; }
                static (S, int) WithStruct(S s) { return (s, s.N); }
                static (S, int) Stored;
                static (S, int) ReturnField() { return Stored; }
                static (int, string) Out2(out int z) { z = 9; return (1, "o"); }
                public static void Main()
                {
                    IGet<(int, int)> g = new Direct();
                    var (a, b) = g.Get(); Console.WriteLine(a + b);
                    var d = new Direct(); var (c, e) = d.Get(); Console.WriteLine(c + e); Console.WriteLine(d.Get().Item2);
                    var w = new Wrap<(int, int)>(); w.V = (5, 6); IGet<(int, int)> g2 = w; Console.WriteLine(g2.Get().Item1 + w.Get().Item2);
                    var (f, h) = Id((7, 8)); Console.WriteLine(f + h);
                    var (i, j) = 3.Twice(); Console.WriteLine(i + j); Console.WriteLine(3.Twice().Item2);
                    var s = new S(); s.N = 1;
                    var (s2, n) = WithStruct(s); s2.N = 50; Console.WriteLine(s.N + "," + n);
                    Stored = (s, 2); var (s3, _) = ReturnField(); s3.N = 60; Console.WriteLine(Stored.Item1.N);
                    var held = ReturnField(); held.Item1.N = 61; Console.WriteLine(Stored.Item1.N);
                    var r = Out2(out var z); Console.WriteLine(r.Item2 + z);
                    var sa = new S[1]; int k = 0;
                    (sa[k].N, k) = (4, 1); Console.WriteLine(sa[0].N + "," + k);
                    var t = (1, 2); (t.Item1, t.Item2) = (t.Item2, t.Item1); Console.WriteLine(t.Item1 + "," + t.Item2);
                    float ff; int gg; var src = (1, 2); (ff, gg) = src; Console.WriteLine(ff / 2 + "," + gg);
                    (int? o1, string o2) = (1, "x"); Console.WriteLine(o1.HasValue + o2);
                }
            }
            """, "P");
        Assert.Equal("3\n3\n2\n11\n15\n9\n6\n1,1\n1\n1\no9\n4,1\n2,1\n0.5,2\nTruex", output);
    }

    [CFact]
    public void Tuples_LibFramesKeepObjectsAlive()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            public class Box { public string Tag = ""; }
            public class P
            {
                static List<(Box, string)> keep = new List<(Box, string)>();
                static (Box, string) held;
                static (Box, string) Make(int k) { var b = new Box(); b.Tag = "t" + k; return (b, b.Tag + "?"); }
                public static void Setup() { }
                public static void Frame()
                {
                    for (var k = 0; k < 30; k++)
                    {
                        var (b, s) = Make(k);
                        keep.Add((b, s));
                        held = Make(k + 1);
                    }
                }
                public static void Report() { Console.WriteLine(keep.Count + keep[keep.Count - 1].Item1.Tag + held.Item2); }
            }
            """;
        Backends.AssertParityLib(source, "P", 5);
    }
}
