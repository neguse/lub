namespace TinyCs.Tests;

/// <summary>
/// List.Add の文位置 lowering (#24): `table.insert(t, v)` ではなく
/// `t[#t + 1] = v` を出力する。値意味論は TranspileAndRun で、出力形は
/// table.insert の有無だけで検査する (完全一致は持たない)。
/// </summary>
public class ListAddIndexTests
{
    private static string Run(string cs, string luaExpr) =>
        TestHelper.TranspileAndRunWithRuntime(cs, luaExpr);

    [Fact]
    public void Add_AsStatement_UsesIndexAssign()
    {
        var cs = """
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int>();
                    list.Add(10);
                    list.Add(20);
                    list.Add(list[0] + list[1]);
                    return list[2] * 10 + list.Count;
                }
            }
            """;
        Assert.Equal("303", Run(cs, "T.test()"));
        Assert.DoesNotContain("table.insert", Transpiler.Transpile(cs));
    }

    [Fact]
    public void Add_InExpressionLambda_UsesIndexAssign()
    {
        var cs = """
            using System;
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var list = new List<string>();
                    Action<string> push = s => list.Add(s);
                    push("a");
                    push("b");
                    return list.Count;
                }
            }
            """;
        Assert.Equal("2", Run(cs, "T.test()"));
        Assert.DoesNotContain("table.insert", Transpiler.Transpile(cs));
    }

    [Fact]
    public void Add_InExpressionBodiedMethod_UsesIndexAssign()
    {
        var cs = """
            using System.Collections.Generic;
            public class T
            {
                private List<int> items = new List<int>();
                public void Push(int v) => items.Add(v);
                public static int Test()
                {
                    var t = new T();
                    t.Push(1);
                    t.Push(2);
                    t.Push(3);
                    return t.items.Count;
                }
            }
            """;
        Assert.Equal("3", Run(cs, "T.test()"));
        Assert.DoesNotContain("table.insert", Transpiler.Transpile(cs));
    }

    [Fact]
    public void Add_OnFieldAndPropertyReceiver()
    {
        var cs = """
            using System.Collections.Generic;
            public class Bag
            {
                public List<int> Field = new List<int>();
                public List<int> Prop { get; } = new List<int>();
            }
            public class T
            {
                public static int Test()
                {
                    var bag = new Bag();
                    bag.Field.Add(1);
                    bag.Field.Add(2);
                    bag.Prop.Add(3);
                    return bag.Field.Count * 10 + bag.Prop[0];
                }
            }
            """;
        Assert.Equal("23", Run(cs, "T.test()"));
        Assert.DoesNotContain("table.insert", Transpiler.Transpile(cs));
    }

    [Fact]
    public void Add_OnMethodCallReceiver_EvaluatesReceiverOnce()
    {
        var cs = """
            using System.Collections.Generic;
            public class T
            {
                private static int calls = 0;
                private static List<int> list = new List<int>();
                private static List<int> Get() { calls++; return list; }
                public static int Test()
                {
                    Get().Add(5);
                    Get().Add(6);
                    return calls * 100 + list.Count * 10 + list[1];
                }
            }
            """;
        Assert.Equal("226", Run(cs, "T.test()"));
    }

    [Fact]
    public void Add_ArgumentWithSideEffect_KeepsCSharpOrder()
    {
        // C# は引数評価 (Grow が list に 1 要素足す) の後に Add する
        var cs = """
            using System.Collections.Generic;
            public class T
            {
                private static List<int> list = new List<int>();
                private static int Grow() { list.Add(1); return 2; }
                public static int Test()
                {
                    list.Add(Grow());
                    return list.Count * 10 + list[1];
                }
            }
            """;
        Assert.Equal("22", Run(cs, "T.test()"));
        Assert.DoesNotContain("table.insert", Transpiler.Transpile(cs));
    }

    [Fact]
    public void Add_NewObjectArgument_UsesIndexAssign()
    {
        var cs = """
            using System.Collections.Generic;
            public class Foo
            {
                public int V;
                public Foo(int v) { V = v; }
            }
            public class T
            {
                public static int Test()
                {
                    var list = new List<Foo>();
                    for (int i = 1; i <= 3; i++) list.Add(new Foo(i * 10));
                    list.Add(new Foo(list.Count));
                    return list[2].V + list[3].V;
                }
            }
            """;
        Assert.Equal("33", Run(cs, "T.test()"));
        Assert.DoesNotContain("table.insert", Transpiler.Transpile(cs));
    }

    [Fact]
    public void Add_MethodCallArgument_UsesIndexAssign()
    {
        var cs = """
            using System.Collections.Generic;
            public class T
            {
                private List<int> items = new List<int>();
                private static int Twice(int v) { return v * 2; }
                private int Next() { return items.Count + 1; }
                public static int Test()
                {
                    var t = new T();
                    t.items.Add(Twice(4));
                    t.items.Add(t.Next());
                    t.items.Add(Twice(t.items.Count));
                    return t.items[0] * 100 + t.items[1] * 10 + t.items[2];
                }
            }
            """;
        Assert.Equal("824", Run(cs, "T.test()"));
        Assert.DoesNotContain("table.insert", Transpiler.Transpile(cs));
    }

    [Fact]
    public void Add_BoundArgument_LocalDoesNotLeakIntoFollowingStatements()
    {
        // 束縛 local が後続の同名参照 (別の Add の束縛) と干渉しないこと
        var cs = """
            using System.Collections.Generic;
            public class T
            {
                private static int Id(int v) { return v; }
                public static int Test()
                {
                    var a = new List<int>();
                    var b = new List<int>();
                    a.Add(Id(1));
                    b.Add(Id(2));
                    a.Add(Id(a[0] + b[0]));
                    return a[1];
                }
            }
            """;
        Assert.Equal("3", Run(cs, "T.test()"));
    }

    [Fact]
    public void Add_OnListOfStruct_CopiesValue()
    {
        var cs = """
            using System.Collections.Generic;
            public struct P { public int X; }
            public class T
            {
                public static int Test()
                {
                    var list = new List<P>();
                    var p = new P();
                    p.X = 1;
                    list.Add(p);
                    p.X = 2;
                    list.Add(p);
                    return list[0].X * 10 + list[1].X;
                }
            }
            """;
        Assert.Equal("12", Run(cs, "T.test()"));
        Assert.DoesNotContain("table.insert", Transpiler.Transpile(cs));
    }

    [Fact]
    public void Add_InsideLoopAndIife_UsesIndexAssign()
    {
        var cs = """
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int>();
                    for (int i = 0; i < 5; i++)
                    {
                        if (i % 2 == 0) list.Add(i * i);
                    }
                    var sum = 0;
                    foreach (var v in list) sum += v;
                    return sum;
                }
            }
            """;
        Assert.Equal("20", Run(cs, "T.test()"));
        Assert.DoesNotContain("table.insert", Transpiler.Transpile(cs));
    }
}
