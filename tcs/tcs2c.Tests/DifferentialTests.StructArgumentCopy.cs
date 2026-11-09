namespace TinyCs.Tcs2c.Tests;

// 可変 struct の引数の copy を省いても、値渡しの意味は変わらない (il-spec §10)。呼ばれた側の書き換え、
// 呼ばれた側が残す値 (field / List / 配列 / 戻り値 / lambda)、呼び出しの間に変わる場所 (field / static /
// 配列の要素 / lambda が捕捉する local / 後の引数と受け手が書き換える local)。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void StructArguments_KeepValueSemantics()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public struct In { public int N; }
            public struct V
            {
                public int X;
                public In I;
                public int[] A;
                public V(int x) { X = x; I = new In(); A = null; }
                public static V operator +(V a, V b) { a.X += b.X; return a; }
                public static V operator -(V a, V b) { return new V(a.X - b.X); }
                public void Inc() { X++; }
                public int Mut() { X = 2; return 7; }
                public readonly int Get() { return X; }
                public int Peek { get { X++; return X; } }
                public int Self(V o) { X += 100; return o.X; }
                public static int Stat(V o) { return o.X; }
            }
            public class C { public V P; public C(V p) { P = p; } public C(V p, int k) { p.X = k; P = p; } }
            public struct W { public V P; public W(V p) { p.X += 1; P = p; } public W(V p, int k) { P = p; } }
            public interface IF { int F(V v); }
            public class FA : IF { public int F(V v) { v.X = 50; return v.X; } }
            public class FB : IF { public int F(V v) { return v.X; } }
            public static class Ext { public static int E(this V v) { v.X = 77; return v.X; } }
            public class Holder { public V P; public V[] Arr = new V[1]; public static V St; }
            public static class M
            {
                public static V Keep;
                public static Holder H = new Holder();
                public static List<V> L = new List<V>();
                public static int WriteField(V v) { v.X = 9; return v.X; }
                public static int WriteNested(V v) { v.I.N = 9; return v.I.N; }
                public static int IncDirect(V v) { v.X++; return v.X; }
                public static int Compound(V v) { v.X += 3; return v.X; }
                public static int Rebind(V v) { v = new V(4); v.X = 5; return v.X; }
                public static int CallMut(V v) { v.Inc(); return v.X; }
                public static int CallReadonly(V v) { return v.Get() + Stat(v); }
                public static int Stat(V v) { return v.X; }
                public static int PeekProp(V v) { return v.Peek; }
                public static int ThroughArray(V v) { v.A[0] = 8; return v.A[0]; }
                public static void Store(V v) { Keep = v; }
                public static void StoreList(V v) { L.Add(v); }
                public static void StoreArr(V v) { H.Arr[0] = v; }
                public static V Ret(V v) { return v; }
                public static V RetExpr(V v) => v;
                public static Func<int> Cap(V v) { return () => v.X; }
                public static Func<int> CapWrite(V v) { return () => { v.X++; return v.X; }; }
                public static int Two(V a, int b) { return a.X * 100 + b; }
                public static int Pair(V a, V b) { a.X = 1000; return b.X; }
                public static int ReadsField(V v) { H.P.X = 42; return v.X; }
                public static int ReadsStatic(V v) { Holder.St.X = 43; return v.X; }
                public static int ReadsArr(V v) { H.Arr[0].X = 44; return v.X; }
                public static int ViaClosure(V v, Action bump) { var b = v.X; bump(); return b * 100 + v.X; }
                public static int Chain(V v) { return Chain2(v) + v.X; }
                public static int Chain2(V v) { v.X = 1; return v.X; }
                public static int Recur(V v, int n) { if (n == 0) return v.X; v.X += n; return Recur(v, n - 1); }
                public static int Pat(V v) { return v is { X: 3 } ? 1 : 0; }
                public static V Sel(bool c, V a, V b) { return c ? a : b; }
                public static int Foreach(V v) { int s = 0; foreach (var i in v.A) s += i + v.X; return s; }
            }
            public static class P
            {
                public static void Main()
                {
                    var a = new V(3);
                    Console.WriteLine(M.WriteField(a) + " " + a.X);
                    Console.WriteLine(M.WriteNested(a) + " " + a.I.N);
                    Console.WriteLine(M.IncDirect(a) + " " + a.X);
                    Console.WriteLine(M.Compound(a) + " " + a.X);
                    Console.WriteLine(M.Rebind(a) + " " + a.X);
                    Console.WriteLine(M.CallMut(a) + " " + a.X);
                    Console.WriteLine(M.CallReadonly(a) + " " + a.X);
                    Console.WriteLine(M.PeekProp(a) + " " + a.X);
                    Console.WriteLine(new C(a, 2).P.X + " " + a.X);
                    var ar = new V(1); ar.A = new int[] { 0 };
                    Console.WriteLine(M.ThroughArray(ar) + " " + ar.A[0]);
                    M.Store(a); a.X = 10; Console.WriteLine(M.Keep.X + " " + a.X);
                    M.StoreList(a); a.X = 11; Console.WriteLine(M.L[0].X);
                    M.StoreArr(a); a.X = 12; Console.WriteLine(M.H.Arr[0].X);
                    var r = M.Ret(a); r.X = 13; Console.WriteLine(a.X + " " + r.X);
                    var re = M.RetExpr(a); re.X = 14; Console.WriteLine(a.X + " " + re.X);
                    a.X = 15; var cap = M.Cap(a); a.X = 16; Console.WriteLine(cap());
                    var capw = M.CapWrite(a); Console.WriteLine(capw() + " " + a.X);
                    // 後の引数と受け手が書き換える
                    a.X = 1;
                    Console.WriteLine(M.Two(a, a.Mut()) + " " + a.X);
                    a.X = 1;
                    Console.WriteLine(M.Pair(a, a) + " " + a.X);
                    a.X = 1;
                    Console.WriteLine(a.Self(a) + " " + a.X);
                    a.X = 1;
                    // 呼び出しの間に変わる場所
                    var h = M.H;
                    h.P.X = 1;
                    Console.WriteLine(M.ReadsField(h.P) + " " + h.P.X);
                    Holder.St.X = 1;
                    Console.WriteLine(M.ReadsStatic(Holder.St) + " " + Holder.St.X);
                    h.Arr[0].X = 1;
                    Console.WriteLine(M.ReadsArr(h.Arr[0]) + " " + h.Arr[0].X);
                    // lambda が捕捉する local
                    var cl = new V(2);
                    Action bump = () => cl.X = 7;
                    Console.WriteLine(M.ViaClosure(cl, bump) + " " + cl.X);
                    // 連鎖と再帰
                    a.X = 5;
                    Console.WriteLine(M.Chain(a) + " " + M.Recur(a, 3) + " " + a.X);
                    // operator / ctor
                    var o1 = new V(1); var o2 = new V(2);
                    var o3 = o1 + o2;
                    Console.WriteLine(o1.X + " " + o2.X + " " + o3.X + " " + (o1 + o1).X + " " + (o1 - o2).X);
                    var c = new C(o1); o1.X = 30; Console.WriteLine(c.P.X);
                    var w = new W(o1); Console.WriteLine(w.P.X + " " + o1.X);
                    var w2 = new W(o1, 0); o1.X = 31; Console.WriteLine(w2.P.X);
                    // interface / 拡張 / delegate / method group
                    var fs = new IF[] { new FA(), new FB() };
                    foreach (var f in fs) Console.WriteLine(f.F(a) + " " + a.X);
                    Console.WriteLine(a.E() + " " + a.X + " " + Ext.E(a) + " " + a.X);
                    Func<V, int> g = M.WriteField;
                    Console.WriteLine(g(a) + " " + a.X);
                    Func<V, int> lam = v => { v.X = 66; return v.X; };
                    Console.WriteLine(lam(a) + " " + a.X);
                    // foreach の変数、parameter のまま渡す、選ぶ
                    var items = new List<V> { new V(1), new V(2) };
                    foreach (var it in items) { Console.WriteLine(M.WriteField(it) + " " + it.X); }
                    Console.WriteLine(items[0].X);
                    Console.WriteLine(M.WriteField(items[1]) + " " + items[1].X);
                    var s = M.Sel(true, a, o2); s.X = 200; Console.WriteLine(a.X + " " + o2.X);
                    a.X = 3; Console.WriteLine(M.Pat(a));
                    ar.X = 2; Console.WriteLine(M.Foreach(ar));
                    Console.WriteLine(V.Stat(a));
                }
            }
            """, "P");
        Assert.Equal("""
            9 3
            9 0
            4 3
            6 3
            5 3
            4 3
            6 3
            4 3
            2 3
            8 8
            3 10
            10
            11
            12 13
            12 14
            15
            17 16
            107 2
            1 1
            1 101
            1 42
            1 43
            1 44
            202 7
            6 11 5
            1 2 3 2 -1
            1
            31 30
            30
            50 5
            5 5
            77 5 77 5
            9 5
            66 5
            9 1
            9 2
            1
            9 2
            5 2
            1
            10
            3
            """.ReplaceLineEndings("\n"), output);
    }
}
