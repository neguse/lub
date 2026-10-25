namespace TinyCs.Tests;

// il-spec §10 の copy 地点のうち、専用の lowering を持つ経路 (initializer /
// foreach / 分解 / with / setter / LINQ / Dictionary)。期待値は C# の値意味論で、
// dotnet differential が CoreCLR と突き合わせる
public class StructCopyPointTests
{
    private const string S = """
        using System;
        using System.Collections.Generic;
        using TinySystem.Linq;
        public struct S { public int N; public void Bump() { N += 1; } }
        """;

    private static string Run(string source) =>
        TestHelper.TranspileAndRunWithRuntime(S + source, "T.Test()");

    [Fact]
    public void ObjectInitializer_CopiesFieldAndPropertyValues()
    {
        Assert.Equal("9:2:2:2:2", Run("""
            public struct O { public S In; }
            public class Box { public S Field; public S Prop { get; set; } }
            public class T
            {
                public static string Test()
                {
                    var s = new S { N = 2 };
                    var box = new Box { Field = s, Prop = s };
                    var o = new O { In = s };
                    var nested = new O { In = { N = 2 } };
                    s.N = 9;
                    return s.N + ":" + box.Field.N + ":" + box.Prop.N + ":" + o.In.N
                        + ":" + nested.In.N;
                }
            }
            """));
    }

    [Fact]
    public void ObjectInitializer_NestedCollection_CopiesItems()
    {
        Assert.Equal("2", Run("""
            public class Box { public List<S> Items = new List<S>(); }
            public class T
            {
                public static string Test()
                {
                    var s = new S { N = 2 };
                    var box = new Box { Items = { s } };
                    s.N = 9;
                    return box.Items[0].N.ToString();
                }
            }
            """));
    }

    [Fact]
    public void Foreach_IterationVariable_IsCopy()
    {
        Assert.Equal("3:3:2:2", Run("""
            public class T
            {
                public static string Test()
                {
                    var arr = new S[] { new S { N = 2 } };
                    var list = new List<S> { new S { N = 2 } };
                    var seen = "";
                    foreach (var x in arr) { x.Bump(); seen += x.N; }
                    foreach (var x in list) { x.Bump(); seen += ":" + x.N; }
                    return seen + ":" + arr[0].N + ":" + list[0].N;
                }
            }
            """));
    }

    [Fact]
    public void Deconstruction_CopiesStructComponents()
    {
        Assert.Equal("7:2:4", Run("""
            public record Pair(S item, int tag);
            public class T
            {
                public static string Test()
                {
                    var p = new Pair(new S { N = 2 }, 4);
                    var (x, tag) = p;
                    x.N = 7;
                    return x.N + ":" + p.item.N + ":" + tag;
                }
            }
            """));
    }

    [Fact]
    public void With_CopiesOverrideValue()
    {
        Assert.Equal("7:2:1:4", Run("""
            public record Box(S item, int tag);
            public class T
            {
                public static string Test()
                {
                    var p = new Box(new S { N = 1 }, 4);
                    var s = new S { N = 2 };
                    var q = p with { item = s };
                    s.N = 7;
                    return s.N + ":" + q.item.N + ":" + p.item.N + ":" + q.tag;
                }
            }
            """));
    }

    [Fact]
    public void CustomSetter_ValueIsCopy_InstanceAndStatic()
    {
        Assert.Equal("2:7:7", Run("""
            public class Holder
            {
                public int Seen;
                public static int StaticSeen;
                public S Item { set { value.N = 7; Seen = value.N; } }
                public static S StaticItem { set { value.N = 7; StaticSeen = value.N; } }
            }
            public class T
            {
                public static string Test()
                {
                    var source = new S { N = 2 };
                    var h = new Holder();
                    h.Item = source;
                    Holder.StaticItem = source;
                    return source.N + ":" + h.Seen + ":" + Holder.StaticSeen;
                }
            }
            """));
    }

    [Fact]
    public void FieldInitializers_CopyValues()
    {
        Assert.Equal("2:2:2:2:9", Run("""
            public class Source { public static S Value = new S { N = 2 }; }
            public class Statics
            {
                public static S Field = Source.Value;
                public static S Prop { get; set; } = Source.Value;
            }
            public class Box
            {
                public S Field = Source.Value;
                public S Prop { get; set; } = Source.Value;
            }
            public class T
            {
                public static string Test()
                {
                    var box = new Box();
                    var warm = Statics.Field.N + Statics.Prop.N;
                    Source.Value.N = 9;
                    return Statics.Field.N + ":" + Statics.Prop.N + ":" + box.Field.N
                        + ":" + box.Prop.N + ":" + Source.Value.N;
                }
            }
            """));
    }

    [Fact]
    public void StaticFieldInitializer_CopiesFieldOfObject()
    {
        Assert.Equal("2:9", Run("""
            public class Holder { public S Field; }
            public class T
            {
                public static Holder Source = new Holder { Field = new S { N = 2 } };
                public static S Saved = Source.Field;
                public static string Test()
                {
                    Saved.N = 9;
                    return Source.Field.N + ":" + Saved.N;
                }
            }
            """));
    }

    [Fact]
    public void ArrayAndListInitializers_CopyElements()
    {
        Assert.Equal("2:9|9:2", Run("""
            public class T
            {
                public static string Test()
                {
                    var s = new S { N = 2 };
                    var a = new S[] { s };
                    a[0].N = 9;
                    var first = s.N + ":" + a[0].N;
                    var t = new S { N = 2 };
                    var l = new List<S> { t };
                    t.N = 9;
                    return first + "|" + t.N + ":" + l[0].N;
                }
            }
            """));
    }

    [Fact]
    public void DictionaryInitializers_CopyValues()
    {
        Assert.Equal("2:2", Run("""
            public class T
            {
                public static string Test()
                {
                    var s = new S { N = 2 };
                    var a = new Dictionary<int, S> { [1] = s };
                    var b = new Dictionary<int, S> { { 1, s } };
                    s.N = 9;
                    return a[1].N + ":" + b[1].N;
                }
            }
            """));
    }

    [Fact]
    public void ArrayToList_CopiesElements()
    {
        Assert.Equal("9:2:2", Run("""
            public class T
            {
                public static string Test()
                {
                    var source = new S[] { new S { N = 2 } };
                    var copy = new List<S>(source);
                    var filtered = new List<S>(source).Where(x => true);
                    source[0].N = 9;
                    return source[0].N + ":" + copy[0].N + ":" + filtered[0].N;
                }
            }
            """));
    }

    [Fact]
    public void LinqLambda_StructParameterIsCopy()
    {
        Assert.Equal("1:3:2:3", Run("""
            public class T
            {
                public static string Test()
                {
                    var xs = new List<S> { new S { N = 3 } };
                    var any = xs.Any(x => { x.Bump(); return x.N == 4; }) ? 1 : 0;
                    var arr = new S[] { new S { N = 2 } };
                    var count = new List<S>(arr).Count(x => { x.N = 5; return true; });
                    var same = new List<S>(arr).Select(x => x);
                    arr[0].N = 3;
                    return any + ":" + xs[0].N + ":" + same[0].N + ":" + arr[0].N
                        + (count == 1 ? "" : "!");
                }
            }
            """));
    }

    [Fact]
    public void DictionaryAddAndTryGetValue_Copy()
    {
        Assert.Equal("3:3", Run("""
            public class T
            {
                public static string Test()
                {
                    var d = new Dictionary<int, S>();
                    var value = new S { N = 3 };
                    d.Add(1, value);
                    value.N = 9;
                    var e = new Dictionary<int, S>();
                    e.Add(1, new S { N = 3 });
                    e.TryGetValue(1, out S got);
                    got.N = 9;
                    return d[1].N + ":" + e[1].N;
                }
            }
            """));
    }

    // static method の method group は runtime に渡しても、delegate 変数や static field を経ても、struct parameter を copy して受ける
    [Fact]
    public void MethodGroup_StructParameterIsCopy()
    {
        Assert.Equal("15:1002:2003|2,1,|1,2,|1:16", Run("""
            public static class Ext
            {
                public static bool BumpE(this S s) { s.N += 100; return true; }
                public static bool AnyBumped(List<S> xs) { return xs.Any(BumpE); }
            }
            public class T
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
                public static string Test()
                {
                    var xs = new List<S> { new S { N = 2 }, new S { N = 1 } };
                    var n = xs.Where(Bump).Count + xs.Count(Bump) + (xs.All(Bump) ? 1 : 0)
                        + (xs.Any(Bump) ? 1 : 0) + xs.FirstOrDefault(Bump).N + xs.Count(Bump) - 1
                        + xs.OrderBy(Key).Count + xs.Select(Id).Count
                        + xs.Select(Key).Count;
                    var max = xs.Max(Key);
                    var sum = xs.Sum(Key);
                    xs.Any(Ext.BumpE);
                    Ext.AnyBumped(xs);
                    xs.Any(Saved);
                    var before = Show(xs);
                    xs.Sort(Cmp);
                    Func<S, bool> f = Bump;
                    xs.Any(f);
                    var v = xs[0];
                    f(v);
                    Func<int, S, int> mixed = Mixed;
                    Func<S, int, S, int> three = Three;
                    var calls = mixed(3, v) + three(v, 5, v);
                    return n + ":" + max + ":" + sum + "|" + before + "|" + Show(xs) + "|" + v.N
                        + ":" + calls;
                }
            }
            """));
    }

    // 同じ method の method group は同じ delegate (C# の == と一致)
    [Fact]
    public void MethodGroup_SameMethodIsEqualDelegate()
    {
        Assert.Equal("1:1:0", Run("""
            public class T
            {
                static bool Bump(S s) { s.N += 10; return true; }
                static bool Other(S s) { return false; }
                public static string Test()
                {
                    Func<S, bool> a = Bump;
                    Func<S, bool> b = Bump;
                    Func<S, bool> c = T.Bump;
                    Func<S, bool> o = Other;
                    return (a == b ? 1 : 0) + ":" + (a == c ? 1 : 0) + ":" + (a == o ? 1 : 0);
                }
            }
            """));
    }

    // module mode は module ごとの define chunk と static initializer の chunk に分かれる。
    // どの chunk で変換しても同じ method なら同じ delegate
    [Fact]
    public void MethodGroup_SameDelegateAcrossModules()
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject([
            ("a.cs", """
                using System;
                using System.Collections.Generic;
                using TinySystem.Linq;
                public class A
                {
                    public static Func<S, bool> Saved = B.Bump;
                    public static int Run()
                    {
                        var xs = new List<S> { new S { N = 1 } };
                        xs.Any(Saved);
                        xs.Any(B.Bump);
                        Func<S, bool> f = B.Bump;
                        return xs[0].N + (f == Saved ? 10 : 0) + (B.Same() ? 100 : 0);
                    }
                }
                """),
            ("b.cs", """
                using System;
                public struct S { public int N; }
                public class B
                {
                    public static bool Bump(S s) { s.N += 1000; return true; }
                    public static bool Same() { Func<S, bool> f = Bump; return f == A.Saved; }
                }
                """),
        ]);
        var output = StaticInitOrderTests.RunSnapshot(StaticInitOrderTests.Snapshot(session),
            """print(reg.types["a.cs#A"].Run())""");
        Assert.Equal(["111"], output);
    }

    // 式本体 (=>) の lambda と method も block 本体の return と同じく struct を copy して返す
    [Fact]
    public void ExpressionBody_ReturnIsCopy()
    {
        Assert.Equal("5:5:5:5:7:7:5", Run("""
            public class T
            {
                static S[] arr = new S[0];
                static S Get(int k) => arr[0];
                static S Pick(int k) => k > 0 ? arr[0] : arr[1];
                public static string Test()
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
                    return d1[0].N + ":" + d2[0].N + ":" + d3[0].N + ":" + d4[0].N + ":" + d4[1].N
                        + ":" + d5[0].N + ":" + d5[1].N;
                }
            }
            """));
    }

    // class の user operator の struct parameter は通常の呼び出しと同じく copy を受ける (二項 / 複合代入)
    [Fact]
    public void ClassOperator_StructArgumentIsCopy()
    {
        Assert.Equal("2:6:4:12:5:103:5", Run("""
            public class M
            {
                public int K;
                public static S operator *(M m, S s) { s.N *= m.K; return s; }
                public static S operator *(S s, M m) { s.N += m.K; return s; }
                public static int operator +(M m, S s) { s.N = 100; return s.N + m.K; }
            }
            public class T
            {
                public static string Test()
                {
                    var m = new M { K = 3 };
                    var x = new S { N = 2 };
                    var z = m * x;
                    var arr = new S[] { new S { N = 4 } };
                    var w = m * arr[0];
                    var y = x * m;
                    var n = m + arr[0];
                    var c = x;
                    c *= m;
                    return x.N + ":" + z.N + ":" + arr[0].N + ":" + w.N + ":" + y.N + ":" + n
                        + ":" + c.N;
                }
            }
            """));
    }

    // LINQ と同名の user 拡張 method は runtime の List.* ではなく通常の拡張呼び出し
    [Fact]
    public void UserExtensionNamedLikeLinq_IsNotRoutedToRuntime()
    {
        Assert.Equal("1:12", Run("""
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
            public class T
            {
                public static string Test()
                {
                    var arr = new S[] { new S { N = 1 }, new S { N = 2 } };
                    return arr.ToList(1).Count + ":" + new List<S>(arr).Where(10);
                }
            }
            """));
    }
}
