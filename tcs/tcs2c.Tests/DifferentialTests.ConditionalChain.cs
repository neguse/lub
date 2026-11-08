namespace TinyCs.Tcs2c.Tests;

// `?.` の when-not-null が呼び出し / member の連鎖 (`x?.M().N()`、`x?.M().P`)
// のとき、連鎖の一番内側は `?.` の receiver に対する呼び出し / member になる。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void ConditionalAccess_ChainAfterBinding()
    {
        var output = Backends.AssertParity("""
            using TinySystem.Text;
            using System;
            using System.Collections.Generic;
            public static class Ext
            {
                public static int Twice(this int x) { return x * 2; }
                public static int Len(this string s) { return s.RuneCount() + 1; }
                public static Q Grow(this Q q) { return new Q { N = q.N + 10 }; }
            }
            public class Q
            {
                public int N;
                public Q Next() { return new Q { N = N + 1 }; }
                public string Name() { return "q" + N; }
            }
            public struct V { public int X; public V Inc() { return new V { X = X + 1 }; } }
            public class H
            {
                public V? MaybeV = new V { X = 4 };
                public string S = "abc";
                public string T = "hey";
                public Q Q = new Q { N = 1 };
                public H Self;
                public List<int> L = new List<int> { 1, 2 };
                public string Z = null;
                public Q QN = null;
            }
            public static class P
            {
                public static void Main()
                {
                    var h = new H();
                    h.Self = h;
                    Console.WriteLine(h.S?.Len().Twice() ?? -1);
                    Console.WriteLine(h.Q?.Grow().Grow().N ?? -1);
                    Console.WriteLine(h.Q?.Next().Next().N ?? -1);
                    Console.WriteLine(h.T?.ToUpper().RuneCount() ?? -1);
                    Console.WriteLine(h.Q?.Next().Name());
                    Console.WriteLine(h.Self?.Q.Next().N ?? -1);
                    Console.WriteLine(h.Self?.S.RuneCount() ?? -1);
                    Console.WriteLine(h.L?.Count.Twice() ?? -1);
                    Console.WriteLine(h.Z?.Len().Twice() ?? -1);
                    Console.WriteLine(h.QN?.Next().Next().N ?? -1);
                    Console.WriteLine(h.Q?.Next()?.Next().N ?? -1);
                    Console.WriteLine(h.S?.RuneCount().ToString());
                    Console.WriteLine(h.L?[1].Twice() ?? -1);
                    Console.WriteLine(h.MaybeV?.X.Twice() ?? -1);
                    Console.WriteLine(h.MaybeV?.Inc().Inc().X ?? -1);
                    Console.WriteLine(h.Q?.Next()!.Next().N ?? -1);
                }
            }
            """, "P");
        Assert.Equal("""
            8
            21
            3
            3
            q2
            2
            3
            4
            -1
            -1
            3
            3
            4
            8
            6
            3
            """.ReplaceLineEndings("\n"), output);
    }
}

public partial class DifferentialTests
{
    // 連鎖の一番内側が変数 (参照型の receiver の field / 配列要素) なら、
    // struct method の変異はその変数に残る。`T?` receiver の値は copy なので残らない
    [CFact]
    public void ConditionalAccess_ChainStructVariableReceiver()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct C
            {
                public int N;
                public void Bump() { N++; }
                public C Next() { N += 10; return this; }
            }
            public struct W { public C In; }
            public class HS
            {
                public C v = new C { N = 1 };
                public W? mw = new W { In = new C { N = 7 } };
                public HS Self;
            }
            public static class P
            {
                public static void Main()
                {
                    var arr = new C[] { new C { N = 5 } };
                    var hs = new HS();
                    hs.Self = hs;
                    hs.Self?.v.Bump();
                    Console.WriteLine(hs.v.N);
                    arr?[0].Bump();
                    Console.WriteLine(arr[0].N);
                    Console.WriteLine((arr?[0].Next().N ?? -1) + ":" + arr[0].N);
                    hs.Self?.mw?.In.Bump();
                    Console.WriteLine(hs.mw.Value.In.N);
                }
            }
            """, "P");
        Assert.Equal("""
            2
            6
            16:16
            7
            """.ReplaceLineEndings("\n"), output);
    }

    // `?.` / `?[]` の結果を既存の local に代入すると、受け手が null なら値なしになる (#435)
    [CFact]
    public void ConditionalAccess_ReassignLocalToNull()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class C { public int N = 5; public string Name = "c"; public int Get() { return N + 1; } }
            public static class P
            {
                public static void Main()
                {
                    List<int> values = new List<int> { 7 };
                    int? n = values?[0];
                    Console.WriteLine(n.HasValue);
                    values = null;
                    n = values?[0];
                    Console.WriteLine(n.HasValue + "," + n.GetValueOrDefault(42));
                    C c = new C();
                    int? f = c?.N;
                    int? g = c?.Get();
                    string s = c?.Name;
                    c = null;
                    f = c?.N;
                    g = c?.Get();
                    s = c?.Name;
                    Console.WriteLine(f.HasValue + "," + g.HasValue + "," + (s == null));
                    var items = new List<C> { new C(), null, new C() };
                    foreach (var it in items)
                    {
                        int? m = it?.N;
                        Console.WriteLine(m.HasValue);
                    }
                }
            }
            """, "P");
        Assert.Equal("True\nFalse,42\nFalse,False,True\nTrue\nFalse\nTrue", output);
    }
}
