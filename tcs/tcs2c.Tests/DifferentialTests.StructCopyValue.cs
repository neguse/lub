namespace TinyCs.Tcs2c.Tests;

// struct の copy 地点 (il-spec §10) のうち ctor の引数・?. 経由の呼び出しの
// 引数・copy constructor の要素・式位置の代入の値・??= の右辺。Lua 向けに足した
// copy (copy constructor の __copy 等) を C backend が同じ値意味論のまま受け取る
// ことも見る
public partial class DifferentialTests
{
    [CFact]
    public void StructCopy_ArgumentsAndAssignmentValues()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public struct S { public int N; }
            public struct Box { public S I; }
            public class D { public S Mine; public S Held; public D(S s) { Mine = s; s.N = 5; Held = s; } }
            public class H
            {
                S stored;
                public Box Data;
                public S Field;
                public S? Opt;
                public S Item { get { return stored; } set { value.N = 7; stored = value; } }
            }
            public class P
            {
                static H Shared = new H();
                static H Get() { return Shared; }
                static void Mut(S x) { x.N = 77; }
                static S Make() { return new S { N = 5 }; }
                public static void Main()
                {
                    var d = new D(new S { N = 1 });
                    List<S> l = new List<S>();
                    var t = new S { N = 1 };
                    l?.Add(t);
                    t.N = 2;
                    Console.WriteLine(d.Mine.N + ":" + d.Held.N + ":" + l[0].N);
                    var arr = new S[] { new S { N = 1 }, new S { N = 3 } };
                    var a = new List<S>(arr);
                    var b = new List<S>(arr).Select(x => x);
                    arr[0].N = 2;
                    arr[1].N = 4;
                    Console.WriteLine(a[0].N + ":" + b[1].N);
                    var h = new H();
                    var src = new S { N = 5 };
                    var y = (h.Item = src);
                    var dict = new Dictionary<int, S>();
                    var list = new List<S> { new S() };
                    var items = new S[1];
                    S loc;
                    Mut(h.Data.I = Make());
                    Mut(h.Field = src);
                    Mut(Get().Field = src);
                    Mut(dict[1] = src);
                    Mut(list[0] = src);
                    Mut(items[0] = src);
                    Mut(loc = src);
                    Console.WriteLine(y.N + ":" + h.Item.N + ":" + h.Data.I.N + ":" + h.Field.N
                        + ":" + Shared.Field.N + ":" + dict[1].N + ":" + list[0].N
                        + ":" + items[0].N + ":" + loc.N + ":" + src.N);
                    h.Opt ??= src;
                    src.N = 9;
                    Console.WriteLine(h.Opt.Value.N);
                }
            }
            """, "P");
        Assert.Equal("1:5:1\n1:3\n5:7:5:5:5:5:5:5:5:5\n5", output);
    }
}
