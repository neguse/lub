namespace TinyCs.Tests;

// データ struct の値意味論 (il-spec §10 の copy 地点)
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

    // ---- struct member (instance method / property / ctor) ----

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

    // static member は引き続きサブセット外 (対応したのは instance member のみ)
    [Fact]
    public void StructStaticMember_ReportsDiagnostic()
    {
        var result = Transpiler.TranspileWithDiagnostics([Vec.Replace(
            "public float Y;",
            "public float Y; public static int Make() { return 1; }")]);
        Assert.Contains(result.Warnings,
            w => w.Contains("StructMember"));
    }

    // struct-in-struct の copy は再帰的でなければならない (shallow だと
    // copy 経由の部分書き込みが alias する)
    [Fact]
    public void NestedStruct_CopyIsDeep()
    {
        var result = TestHelper.TranspileAndRun("""
            public struct Inner
            {
                public int V;
            }
            public struct Outer
            {
                public Inner I;
            }
            public class T
            {
                public static string Test()
                {
                    var a = new Outer();
                    a.I.V = 1;
                    var b = a;
                    b.I.V = 99;
                    return $"{a.I.V}|{b.I.V}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("1|99", result);
    }

    // ---- record struct ----

    [Fact]
    public void RecordStruct_PositionalCreate_AndNewIsZero()
    {
        var result = TestHelper.TranspileAndRun("""
            public record struct Pair(int A, string B);
            public class T
            {
                public static string Test()
                {
                    var p = new Pair(3, "sx");
                    var z = new Pair();
                    return $"{p.A}|{p.B}|{z.A}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("3|sx|0", result);
    }

    [Fact]
    public void RecordStruct_ValueEquality()
    {
        var result = TestHelper.TranspileAndRun("""
            public record struct Pair(int A, string B);
            public class T
            {
                public static string Test()
                {
                    var a = new Pair(1, "s");
                    var b = new Pair(1, "s");
                    var c = new Pair(2, "s");
                    return $"{a == b}|{a == c}|{a != c}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("true|false|true", result);
    }

    [Fact]
    public void RecordStruct_With_CopiesAndOverrides()
    {
        var result = TestHelper.TranspileAndRun("""
            public record struct Pair(int A, string B);
            public class T
            {
                public static string Test()
                {
                    var a = new Pair(1, "x");
                    var c = a with { A = 9 };
                    return $"{a.A}|{c.A}|{c.B}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("1|9|x", result);
    }

    [Fact]
    public void RecordStruct_AssignmentCopies()
    {
        var result = TestHelper.TranspileAndRun("""
            public record struct Pair(int A, string B);
            public class T
            {
                public static string Test()
                {
                    var a = new Pair(1, "x");
                    var b = a;
                    b.A = 5;
                    return $"{a.A}|{b.A}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("1|5", result);
    }

    // ネストした struct 値は推移的フィールド展開で値比較する
    [Fact]
    public void RecordStruct_NestedStructValueEquality()
    {
        var result = TestHelper.TranspileAndRun("""
            public struct Inner
            {
                public int V;
            }
            public record struct Outer(Inner I, int K);
            public class T
            {
                public static string Test()
                {
                    var i = new Inner();
                    i.V = 7;
                    var a = new Outer(i, 1);
                    var b = new Outer(i, 1);
                    i.V = 8;
                    var c = new Outer(i, 1);
                    return $"{a == b}|{a == c}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("true|false", result);
    }

    // ---- readonly struct の copy 省略 ----

    // 不変なら alias しても観測不能 — copy 地点の型別 __copy を全省略する
    [Fact]
    public void ReadonlyStruct_ElidesCopies_MutableKeepsThem()
    {
        const string readonlySrc = """
            public readonly struct R
            {
                public readonly int X;
                public R(int x) { X = x; }
            }
            public class T
            {
                static int Take(R r) { return r.X; }
                public static int Test()
                {
                    var a = new R(3);
                    var b = a;
                    return Take(b) + a.X;
                }
            }
            """;
        // copy 呼び出しは R.__copy(。定義 (function R.__copy(s)) が常に
        // 1 回出るため、呼び出しサイトの有無は出現数で判定する
        static int CountCopies(string lua) =>
            System.Text.RegularExpressions.Regex.Matches(lua,
                System.Text.RegularExpressions.Regex.Escape("R.__copy("))
                .Count;
        Assert.Equal(1, CountCopies(Transpiler.Transpile(readonlySrc)));

        var mutableLua = Transpiler.Transpile(
            readonlySrc.Replace("public readonly struct R",
                "public struct R").Replace("public readonly int X",
                "public int X"));
        Assert.True(CountCopies(mutableLua) > 1,
            "mutable struct should keep copy sites");
    }

    [Fact]
    public void ReadonlyStruct_ValueFlowStaysCorrect()
    {
        var result = TestHelper.TranspileAndRun("""
            public readonly struct R
            {
                public readonly int X;
                public R(int x) { X = x; }
                public int Plus(int d) { return X + d; }
            }
            public class T
            {
                static R Bump(R r) { return new R(r.X + 10); }
                public static string Test()
                {
                    var a = new R(1);
                    var b = Bump(a);
                    return $"{a.X}|{b.X}|{b.Plus(5)}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("1|11|16", result);
    }

    [Fact]
    public void ReadonlyRecordStruct_CreateReadEquality()
    {
        var result = TestHelper.TranspileAndRun("""
            public readonly record struct Rp(int X, int Y);
            public class T
            {
                public static string Test()
                {
                    var a = new Rp(2, 3);
                    var b = new Rp(2, 3);
                    return $"{a.X + a.Y}|{a == b}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("5|true", result);
    }
}
