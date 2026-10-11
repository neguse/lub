namespace TinyCs.Tests;

// il-spec §10 の copy 地点のうち、base(...) の引数・?. 経由の呼び出しの引数・
// ToDictionary の値・式位置の代入の値。期待値は C# の値意味論で、dotnet
// differential が CoreCLR と突き合わせる
public class StructCopyValueTests
{
    private const string S = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        public struct S { public int N; }
        """;

    private static string Run(string source) =>
        TestHelper.TranspileAndRunWithRuntime(S + source, "T.Test()");

    [Fact]
    public void BaseConstructorArgument_IsCopy()
    {
        Assert.Equal("1:5", Run("""
            public class B { public S Held; public B(S s) { s.N = 5; Held = s; } }
            public class D : B { public S Mine; public D(S s) : base(s) { Mine = s; } }
            public class T
            {
                public static string Test()
                {
                    var d = new D(new S { N = 1 });
                    return d.Mine.N + ":" + d.Held.N;
                }
            }
            """));
    }

    [Fact]
    public void ConditionalAccessCall_CopiesArguments()
    {
        Assert.Equal("1:1:2", Run("""
            public class Sink { public S Got; public void Take(S s) { Got = s; s.N = 9; } }
            public class T
            {
                public static string Test()
                {
                    List<S> l = new List<S>();
                    var sink = new Sink();
                    var t = new S { N = 1 };
                    l?.Add(t);
                    sink?.Take(t);
                    t.N = 2;
                    return l[0].N + ":" + sink.Got.N + ":" + t.N;
                }
            }
            """));
    }

    [Fact]
    public void ToDictionary_CopiesElementValues()
    {
        Assert.Equal("1:3", Run("""
            public class T
            {
                public static string Test()
                {
                    var arr = new S[] { new S { N = 1 }, new S { N = 3 } };
                    var a = arr.ToDictionary(x => x.N);
                    var b = arr.ToDictionary(x => x.N + 10, x => x);
                    arr[0].N = 2;
                    arr[1].N = 4;
                    return a[1].N + ":" + b[13].N;
                }
            }
            """));
    }

    [Fact]
    public void SetterAssignmentValue_IsAssignedValue()
    {
        Assert.Equal("1:7:1:7", Run("""
            public class H
            {
                S stored;
                public S Item { get { return stored; } set { value.N = 7; stored = value; } }
                public static S Shared;
                public static S Static { get { return Shared; } set { value.N = 7; Shared = value; } }
            }
            public class T
            {
                public static string Test()
                {
                    var h = new H();
                    var src = new S { N = 1 };
                    var y = (h.Item = src);
                    var z = (H.Static = new S { N = 1 });
                    return y.N + ":" + h.Item.N + ":" + z.N + ":" + H.Static.N;
                }
            }
            """));
    }

    // 代入式の値は格納した slot ではなく値の copy。値を受けた側の変更は
    // 格納先に残らない
    [Fact]
    public void AssignmentValue_IsCopyOfStoredValue()
    {
        Assert.Equal("5:5:5:5:5:5:5", Run("""
            public struct Box { public S I; }
            public class Holder { public Box Data; public S Field; }
            public class T
            {
                static void Mut(S x) { x.N = 77; }
                static S Make() { return new S { N = 5 }; }
                public static string Test()
                {
                    var h = new Holder();
                    var src = new S { N = 5 };
                    var d = new Dictionary<int, S>();
                    var l = new List<S> { new S() };
                    var a = new S[1];
                    S loc;
                    Mut(h.Data.I = Make());
                    Mut(h.Field = src);
                    Mut(d[1] = src);
                    Mut(l[0] = src);
                    Mut(a[0] = src);
                    Mut(loc = src);
                    return h.Data.I.N + ":" + h.Field.N + ":" + d[1].N + ":" + l[0].N
                        + ":" + a[0].N + ":" + loc.N + ":" + src.N;
                }
            }
            """));
    }

    // S? の custom property への ??= の値 (型は S) も copy。値を受けた側の
    // 変更は格納先に残らない
    [Fact]
    public void CoalesceAssignToNullableStructProperty_ValueIsCopy()
    {
        Assert.Equal("3:3", Run("""
            public class H { S? c; public S? CustOpt { get { return c; } set { c = value; } } }
            public class T
            {
                static int Mut(S x) { x.N = 77; return x.N; }
                public static string Test()
                {
                    var h = new H();
                    var src = new S { N = 3 };
                    Mut(h.CustOpt ??= src);
                    return h.CustOpt.Value.N + ":" + src.N;
                }
            }
            """));
    }
}
