namespace TinyCs.Tcs2c.Tests;

// struct の copy 地点 (il-spec §10) のうち専用 lowering を持つ経路の 2 backend
// differential。Lua 向けに足した copy (lambda 入口 / foreach 変数 / ToList の
// __copy 引数) を C backend が同じ値意味論のまま受け取ることも見る
public partial class DifferentialTests
{
    [CFact]
    public void StructCopy_LoweredPathsKeepValueSemantics()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public struct S { public int N; public void Bump() { N += 1; } }
            public readonly struct R { public readonly int V; public R(int v) { V = v; } }
            public record Pair(S Item, int Tag);
            public class Box { public S Field; public List<S> Items = new List<S>(); }
            public class Holder { public int Seen; public S Item { set { value.N = 7; Seen = value.N; } } }
            public class Source { public static S Value = new S { N = 2 }; }
            public class Init { public S Saved = Source.Value; }
            public class P
            {
                public static void Main()
                {
                    var s = new S { N = 2 };
                    var box = new Box { Field = s, Items = { s } };
                    var arr = new S[] { s };
                    var list = new List<S> { s };
                    var dict = new Dictionary<int, S> { [1] = s };
                    dict.Add(2, s);
                    var pair = new Pair(s, 4);
                    var with = pair with { Item = s };
                    var h = new Holder();
                    h.Item = s;
                    var init = new Init();
                    s.N = 9;
                    Source.Value.N = 9;
                    Console.WriteLine(box.Field.N + ":" + box.Items[0].N + ":" + arr[0].N + ":" + list[0].N + ":" + dict[1].N + ":" + dict[2].N + ":" + with.Item.N + ":" + h.Seen + ":" + init.Saved.N);
                    foreach (var x in arr) { x.Bump(); }
                    var (item, tag) = pair;
                    item.N = 5;
                    dict.TryGetValue(1, out S got);
                    got.N = 6;
                    Console.WriteLine(arr[0].N + ":" + pair.Item.N + ":" + tag + ":" + dict[1].N);
                    var copy = new List<S>(arr);
                    var any = list.Any(x => { x.Bump(); return x.N == 3; });
                    var picked = new List<S>(arr).Where(x => { x.N = 8; return true; });
                    arr[0].N = 1;
                    Console.WriteLine(copy[0].N + ":" + any + ":" + list[0].N + ":" + picked[0].N + ":" + picked.Count);
                    var rs = new R[] { new R(3) };
                    Console.WriteLine(new List<R>(rs)[0].V + ":" + new List<R>(rs).Count(r => r.V == 3));
                }
            }
            """, "P");
        Assert.Equal("2:2:2:2:2:2:2:7:2\n2:2:4:2\n2:true:2:2:1\n3:1", output);
    }

    // static method の method group の struct parameter は copy (Lua は変換地点で copy する wrapper を返す、C は値渡しの thunk)
    [CFact]
    public void StructCopy_MethodGroupParameter()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public struct S { public int N; }
            public static class Ext
            {
                public static bool BumpE(this S s) { s.N += 100; return true; }
                public static bool AnyBumped(List<S> xs) { return xs.Any(BumpE); }
            }
            public class P
            {
                static Func<S, bool> Saved = Bump;
                static bool Bump(S s) { s.N += 10; return true; }
                static int Key(S s) { s.N += 1000; return s.N; }
                static int Cmp(S a, S b) { a.N += 1; b.N += 1; return a.N - b.N; }
                static S Id(S s) { s.N += 7; return s; }
                static int Mixed(int k, S s) { s.N += k; return s.N; }
                static int Three(S a, int k, S b) { a.N += k; b.N += k; return a.N + b.N; }
                static string Show(List<S> xs)
                {
                    var r = "";
                    foreach (var x in xs) r += x.N + ",";
                    return r;
                }
                public static void Main()
                {
                    var xs = new List<S> { new S { N = 2 }, new S { N = 1 } };
                    var n = xs.Where(Bump).Count + xs.Count(Bump) + (xs.All(Bump) ? 1 : 0)
                        + (xs.Any(Bump) ? 1 : 0) + xs.FirstOrDefault(Bump).N + xs.Count(Bump) - 1
                        + xs.OrderBy(Key).Count + xs.Select(Id).Count
                        + xs.Select(Key).Count;
                    Console.WriteLine(n + ":" + xs.Max(Key) + ":" + xs.Sum(Key));
                    xs.Any(Ext.BumpE);
                    Ext.AnyBumped(xs);
                    xs.Any(Saved);
                    Console.WriteLine(Show(xs));
                    xs.Sort(Cmp);
                    Func<S, bool> f = Bump;
                    xs.Any(f);
                    var v = xs[0];
                    f(v);
                    Func<int, S, int> mixed = Mixed;
                    Func<S, int, S, int> three = Three;
                    var calls = mixed(3, v) + three(v, 5, v);
                    Console.WriteLine(Show(xs) + "|" + v.N + ":" + calls);
                }
            }
            """, "P");
        Assert.Equal("15:1002:2003\n2,1,\n1,2,|1:16", output);
    }

    // 式本体 (=>) の lambda と method の return は struct を copy する (Select は selector の戻り値をそのまま格納する)
    [CFact]
    public void StructCopy_ExpressionBodyReturn()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public struct S { public int N; }
            public class P
            {
                static S[] arr = new S[0];
                static S Get(int k) => arr[0];
                static S Pick(int k) => k > 0 ? arr[0] : arr[1];
                public static void Main()
                {
                    arr = new S[] { new S { N = 5 }, new S { N = 7 } };
                    var xs = new List<int> { 1, 0 };
                    var d1 = xs.Select(x => arr[0]);
                    var d2 = xs.Select(x => { return arr[0]; });
                    var d3 = xs.Select(Get);
                    var d4 = xs.Select(Pick);
                    Func<int, S> f = x => arr[x];
                    var d5 = xs.Select(f);
                    Action<int> act = x => Get(x);
                    act(1);
                    arr[0].N = 6;
                    arr[1].N = 8;
                    Console.WriteLine(d1[0].N + ":" + d2[0].N + ":" + d3[0].N + ":" + d4[0].N + ":" + d4[1].N + ":" + d5[0].N + ":" + d5[1].N);
                }
            }
            """, "P");
        Assert.Equal("5:5:5:5:7:7:5", output);
    }

    // class の user operator の struct parameter は copy (Lua は呼び出し側で copy を渡す、C は値渡し)
    [CFact]
    public void StructCopy_ClassOperatorArgument()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct V { public int X; }
            public class M
            {
                public int K;
                public static V operator *(M m, V v) { v.X *= m.K; return v; }
                public static V operator *(V v, M m) { v.X += m.K; return v; }
                public static int operator +(M m, V v) { v.X = 100; return v.X + m.K; }
            }
            public class P
            {
                public static void Main()
                {
                    var m = new M { K = 3 };
                    var x = new V { X = 2 };
                    var z = m * x;
                    var arr = new V[] { new V { X = 4 } };
                    var w = m * arr[0];
                    var y = x * m;
                    var n = m + arr[0];
                    var c = x;
                    c *= m;
                    Console.WriteLine(x.X + ":" + z.X + ":" + arr[0].X + ":" + w.X + ":" + y.X + ":" + n + ":" + c.X);
                }
            }
            """, "P");
        Assert.Equal("2:6:4:12:5:103:5", output);
    }

    // LINQ と同名の user 拡張 method (ToList(int) / Where(int)) は自前の実装を呼ぶ
    [CFact]
    public void StructCopy_UserExtensionNamedLikeLinq()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public struct S { public int N; }
            public static class Ext
            {
                public static List<S> ToList(this S[] xs, int take)
                {
                    var r = new List<S>();
                    for (var i = 0; i < take; i++) r.Add(xs[i]);
                    return r;
                }
                public static int Where(this List<S> xs, int k) { return xs.Count + k; }
            }
            public class P
            {
                public static void Main()
                {
                    var arr = new S[] { new S { N = 1 }, new S { N = 2 } };
                    Console.WriteLine(arr.ToList(1).Count);
                    Console.WriteLine(new List<S>(arr).Where(10));
                }
            }
            """, "P");
        Assert.Equal("1\n12", output);
    }
}
