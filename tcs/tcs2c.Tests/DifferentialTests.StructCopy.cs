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
            using System.Linq;
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
                    var copy = arr.ToList();
                    var any = list.Any(x => { x.Bump(); return x.N == 3; });
                    var picked = arr.Where(x => { x.N = 8; return true; }).ToList();
                    arr[0].N = 1;
                    Console.WriteLine(copy[0].N + ":" + any + ":" + list[0].N + ":" + picked[0].N + ":" + picked.Count);
                    var rs = new R[] { new R(3) };
                    Console.WriteLine(rs.ToList()[0].V + ":" + rs.Count(r => r.V == 3));
                }
            }
            """, "P");
        Assert.Equal("2:2:2:2:2:2:2:7:2\n2:2:4:2\n2:true:2:2:1\n3:1", output);
    }

    // LINQ と同名の user 拡張 method (ToList(int) / Where(int)) は自前の実装を呼ぶ
    [CFact]
    public void StructCopy_UserExtensionNamedLikeLinq()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using System.Linq;
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
                    Console.WriteLine(arr.ToList().Where(10));
                }
            }
            """, "P");
        Assert.Equal("1\n12", output);
    }
}
