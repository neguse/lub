namespace TinyCs.Tests;

// T219 (M5 v1): データ struct の値意味論 (il-spec §10 の copy 地点)
public class StructSemanticsTests
{
    private const string Vec = """
        public struct Vec2
        {
            public float X;
            public float Y;
        }
        """;

    [Fact]
    public void Assignment_Copies()
    {
        var result = TestHelper.TranspileAndRun(Vec + """
            public class T
            {
                public static string Test()
                {
                    var a = new Vec2();
                    a.X = 1.0f;
                    var b = a;
                    b.X = 99.0f;
                    return $"{a.X}|{b.X}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("1|99", result);
    }

    [Fact]
    public void ArgumentAndReturn_Copy()
    {
        var result = TestHelper.TranspileAndRun(Vec + """
            public class T
            {
                static Vec2 Bump(Vec2 v)
                {
                    v.X = v.X + 10.0f;
                    return v;
                }
                public static string Test()
                {
                    var a = new Vec2();
                    a.X = 1.0f;
                    var b = Bump(a);
                    return $"{a.X}|{b.X}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("1|11", result);
    }

    // C# の struct 型 field の default は zero 初期化された struct 値
    // (nil にすると member アクセスが実行時エラーになる)
    [Fact]
    public void ClassField_DefaultsToZeroedStruct()
    {
        var result = TestHelper.TranspileAndRun(Vec + """
            public class Holder
            {
                public Vec2 Pos;
                public static string Test()
                {
                    var h = new Holder();
                    return $"{h.Pos.X}|{h.Pos.Y}";
                }
            }
            """, "Holder.Test()", differential: false);
        Assert.Equal("0|0", result);
    }

    [Fact]
    public void ArrayElement_InPlaceWrite_AndReadCopies()
    {
        var result = TestHelper.TranspileAndRun(Vec + """
            public class T
            {
                public static string Test()
                {
                    var arr = new Vec2[2];
                    arr[0] = new Vec2();
                    arr[0].X = 5.0f;
                    var p = arr[0];
                    p.X = 42.0f;
                    return $"{arr[0].X}|{p.X}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("5|42", result);
    }

    // ---- T219b(a): struct member (instance method / property / ctor) ----

    private const string Counter = """
        public struct Counter
        {
            public int N;
            public void Inc() { N = N + 1; }
            public int Twice() { return N * 2; }
        }
        """;

    // 変数レシーバへのメソッド呼び出しは変数自体を変異させる (C# と一致)
    [Fact]
    public void StructMethod_MutatesReceiverVariable()
    {
        var result = TestHelper.TranspileAndRun(Counter + """
            public class T
            {
                public static string Test()
                {
                    var c = new Counter();
                    c.Inc();
                    c.Inc();
                    return $"{c.N}|{c.Twice()}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("2|4", result);
    }

    // List indexer は property (rvalue) — コピーへの変異は捨てられる
    [Fact]
    public void StructMethod_OnListIndexer_MutatesCopyOnly()
    {
        var result = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;
            """ + Counter + """
            public class T
            {
                public static int Test()
                {
                    var list = new List<Counter>();
                    list.Add(new Counter());
                    list[0].Inc();
                    return list[0].N;
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("0", result);
    }

    // 配列要素は variable — 変異が残る
    [Fact]
    public void StructMethod_OnArrayElement_MutatesInPlace()
    {
        var result = TestHelper.TranspileAndRun(Counter + """
            public class T
            {
                public static int Test()
                {
                    var arr = new Counter[1];
                    arr[0] = new Counter();
                    arr[0].Inc();
                    return arr[0].N;
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("1", result);
    }

    // 明示 ctor は zero 初期化後に本文実行。`new S()` は ctor を通らず zero
    [Fact]
    public void StructCtor_InitializesFields_AndNewStaysZero()
    {
        var result = TestHelper.TranspileAndRun("""
            public struct V
            {
                public int X;
                public int Y;
                public V(int x) { X = x * 2; Y = 1; }
            }
            public class T
            {
                public static string Test()
                {
                    var a = new V(21);
                    var b = new V();
                    return $"{a.X}|{a.Y}|{b.X}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("42|1|0", result);
    }

    [Fact]
    public void StructAutoProperty_ReadWriteAndCompound()
    {
        var result = TestHelper.TranspileAndRun("""
            public struct P
            {
                public int V { get; set; }
            }
            public class T
            {
                public static int Test()
                {
                    var p = new P();
                    p.V = 5;
                    p.V += 2;
                    return p.V;
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("7", result);
    }

    [Fact]
    public void StructCustomProperty_UsesAccessors()
    {
        var result = TestHelper.TranspileAndRun("""
            public struct B
            {
                public int Raw;
                public int Doubled
                {
                    get { return Raw * 2; }
                    set { Raw = value; }
                }
            }
            public class T
            {
                public static string Test()
                {
                    var b = new B();
                    b.Doubled = 21;
                    return $"{b.Raw}|{b.Doubled}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("21|42", result);
    }

    // static member は引き続きサブセット外 (T219b(a) は instance member のみ)
    [Fact]
    public void StructStaticMember_ReportsDiagnostic()
    {
        var result = Transpiler.TranspileWithDiagnostics([Vec.Replace(
            "public float Y;",
            "public float Y; public static int Make() { return 1; }")]);
        Assert.Contains(result.Warnings,
            w => w.Contains("StructMember"));
    }
}
