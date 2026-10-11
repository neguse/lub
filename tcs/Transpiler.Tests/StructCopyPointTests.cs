namespace TinyCs.Tests;

// il-spec §10 の copy 地点のうち、専用の lowering を持つ経路 (initializer /
// foreach / 分解 / with / setter / LINQ / Dictionary)。期待値は C# の値意味論で、
// dotnet differential が CoreCLR と突き合わせる
public class StructCopyPointTests
{
    private const string S = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
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
                    var copy = source.ToList();
                    var filtered = source.Where(x => true).ToList();
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
                    var count = arr.Count(x => { x.N = 5; return true; });
                    var same = arr.Select(x => x).ToList();
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
                    return arr.ToList(1).Count + ":" + arr.ToList().Where(10);
                }
            }
            """));
    }
}
