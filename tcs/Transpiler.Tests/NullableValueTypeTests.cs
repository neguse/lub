namespace TinyCs.Tests;

public class NullableValueTypeTests
{
    [Fact]
    public void NullableInt_AssignNull()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    int? x = null;
                    return x == null ? "nil" : "not nil";
                }
            }
            """,
            "T.test()");
        Assert.Equal("nil", result);
    }

    [Fact]
    public void NullableInt_AssignValue()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    int? x = 42;
                    return x ?? 0;
                }
            }
            """,
            "T.test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void NullableInt_HasValue_True()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static bool Test()
                {
                    int? x = 42;
                    return x.HasValue;
                }
            }
            """,
            "tostring(T.test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void NullableInt_HasValue_False()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static bool Test()
                {
                    int? x = null;
                    return x.HasValue;
                }
            }
            """,
            "tostring(T.test())");
        Assert.Equal("false", result);
    }

    [Fact]
    public void NullableInt_Value()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    int? x = 42;
                    return x.Value;
                }
            }
            """,
            "T.test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void NullableInt_GetValueOrDefault()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    int? x = null;
                    return x.GetValueOrDefault();
                }
            }
            """,
            "T.test()");
        Assert.Equal("0", result);
    }

    [Fact]
    public void NullableInt_GetValueOrDefault_WithValue()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    int? x = 99;
                    return x.GetValueOrDefault();
                }
            }
            """,
            "T.test()");
        Assert.Equal("99", result);
    }

    [Fact]
    public void NullableBool_GetValueOrDefault()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static bool Test()
                {
                    bool? b = null;
                    return b.GetValueOrDefault();
                }
            }
            """,
            "tostring(T.test())");
        Assert.Equal("false", result);
    }

    // Lua の `or` は false も fallback してしまうため、bool? の ?? は
    // 明示 nil 判定にする。?? の右辺は null のときだけ評価される。
    [Fact]
    public void NullableBool_Coalesce_FalseIsNotFallback()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    bool? f = false;
                    bool? n = null;
                    var a = f ?? true;
                    var b = n ?? true;
                    return $"{a}|{b}";
                }
            }
            """,
            "T.test()");
        Assert.Equal("false|true", result);
    }

    [Fact]
    public void Coalesce_RightHandSide_EvaluatedOnlyWhenNull()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Calls;

                public static int Fb()
                {
                    Calls = Calls + 1;
                    return 9;
                }

                public static string Test()
                {
                    int? has = 5;
                    int? none = null;
                    var a = has ?? Fb();
                    var b = none ?? Fb();
                    return $"{Calls}|{a}|{b}";
                }
            }
            """,
            "T.test()");
        Assert.Equal("1|5|9", result);
    }

    [Fact]
    public void GetValueOrDefault_ExplicitFallback_UsedOnlyWhenNull()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    bool? f = false;
                    int? none = null;
                    int? has = 3;
                    var a = f.GetValueOrDefault(true);
                    var b = none.GetValueOrDefault(5);
                    var c = has.GetValueOrDefault(7);
                    return $"{a}|{b}|{c}";
                }
            }
            """,
            "T.test()");
        Assert.Equal("false|5|3", result);
    }

    [Fact]
    public void GetValueOrDefault_FallbackArgument_AlwaysEvaluatedOnce()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Calls;

                public static int Fb()
                {
                    Calls = Calls + 1;
                    return 9;
                }

                public static string Test()
                {
                    int? has = 3;
                    var a = has.GetValueOrDefault(Fb());
                    return $"{Calls}|{a}";
                }
            }
            """,
            "T.test()");
        Assert.Equal("1|3", result);
    }

    // lifted 演算子 (C# §12.4.8): 片方でも null なら null、比較は false、
    // ==/!= は両 null で等しい、bool? の & | は三値論理。dotnet differential
    // が実 .NET と突き合わせる
    [Fact]
    public void Lifted_ArithmeticAndComparison()
    {
        var result = TestHelper.TranspileAndRun("""
            public static class T
            {
                public static string Show(int? v) => v.HasValue ? "v=" + v.Value.ToString() : "none";
                public static string Test()
                {
                    int? a = 10;
                    int? n = null;
                    int b = 3;
                    int? s1 = a + b;
                    int? s2 = n + b;
                    int? s3 = a * a - b;
                    int? s4 = a / 3;
                    int? s5 = a % 3;
                    int? s6 = -a;
                    int? s7 = n * 2;
                    int? c = 1;
                    c++;
                    c += 5;
                    c = c << 2;
                    c--;
                    n++;
                    return Show(s1) + "|" + Show(s2) + "|" + Show(s3) + "|" + Show(s4) + "|" + Show(s5)
                        + "|" + Show(s6) + "|" + Show(s7) + "|" + Show(c) + "|" + Show(n) + "|" + Show(c & 6)
                        + "|" + Show(~c) + "|"
                        + (a == 10) + ":" + (n == 10) + ":" + (a != n) + ":" + (n == n) + ":" + (a < 20)
                        + ":" + (n < 20) + ":" + (a >= 10) + ":" + (n >= 0) + ":" + (a > n);
                }
            }
            """, "T.test()");
        Assert.Equal("v=13|none|v=97|v=3|v=1|v=-10|none|v=27|none|v=2|v=-28|" +
            "True:False:True:True:True:False:True:False:False".ToLowerInvariant(), result);
    }

    [Fact]
    public void Lifted_BoolThreeValuedAndFloat()
    {
        var result = TestHelper.TranspileAndRun("""
            public static class T
            {
                public static string Test()
                {
                    bool? t = true;
                    bool? u = false;
                    bool? v = null;
                    float? f = 1.5f;
                    float? g = null;
                    return (t & v) + "|" + (u & v) + "|" + (t | v) + "|" + (u | v) + "|" + (v & v) + "|" + (!v)
                        + "|" + (!t) + "|" + (t ^ u) + "|" + (t ^ v) + "|" + (f * 2f) + "|" + (g * 2f)
                        + "|" + (f / 4f) + "|" + (f == 1.5f) + "|" + (g == null) + "|" + $"[{f}][{g}][{v}]";
                }
            }
            """, "T.test()");
        Assert.Equal("|false|true||||false|true||3||0.375|true|true|[1.5][][]", result);
    }

    [Fact]
    public void Value_OnNull_Faults()
    {
        var lua = Transpiler.Transpile("""
            public static class T
            {
                public static int Test()
                {
                    int? n = null;
                    return n.Value;
                }
            }
            """);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TestHelper.RunLua(lua + "\nprint(T.test())"));
        Assert.Contains("Nullable object must have a value", ex.Message);
    }

    // `?.` の receiver が `S?` のときは .Value (copy) に対する member 参照、
    // 参照型 receiver の値型 member は T? に wrap。`??=` と bool? の文字列化も
    // nil 比較を使わない明示ノード経路。実 .NET と一致 (dotnet differential)
    [Fact]
    public void ConditionalAccess_OnNullableStruct_AndCoalesceAssign()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            using System.Collections.Generic;
            public struct Vec { public int X; public int Y; public int Sum() => X + Y; public void Bump() { X = X + 100; } public int Twice => X * 2; }
            public readonly record struct Ro(int A) { public int Dbl() => A * 2; }
            public class Node { public int Hp; public int? Mana; public Node Next; public string Name = "n"; public Vec Pos; public int Len() => Name.Length; public Node Self() => this; }
            public class T
            {
                public static string Show(int? v) => v.HasValue ? "v=" + v.Value.ToString() : "none";
                public static string Test()
                {
                    var s = "";
                    Vec? v = new Vec { X = 1, Y = 2 };
                    Vec? nv = null;
                    int? a = v?.X;
                    int? b = nv?.X;
                    int? c = v?.Sum();
                    int? d = nv?.Sum();
                    int? t = v?.Twice;
                    v?.Bump();
                    s += (Show(a) + "|" + Show(b) + "|" + Show(c) + "|" + Show(d) + "|" + Show(t) + "|" + Show(v?.X)) + "/";
                    Node n = new Node { Hp = 3 };
                    n.Pos.X = 9;
                    Node nn = null;
                    int? e = n?.Hp;
                    int? f = nn?.Hp;
                    int? g = n?.Len();
                    string h = nn?.Name;
                    string h2 = n?.Name;
                    int? m = n?.Mana;
                    int? k = n?.Next?.Hp;
                    int? k2 = n?.Self()?.Hp;
                    int? px = n?.Pos.X;
                    Vec? pv = nn?.Pos;
                    s += (Show(e) + "|" + Show(f) + "|" + Show(g) + "|" + (h == null) + "|" + h2 + "|" + Show(m) + "|" + Show(k) + "|" + Show(k2) + "|" + Show(px) + "|" + pv.HasValue) + "/";
                    int? q = null; q ??= 4;
                    int? r = 9; r ??= 1;
                    bool? bq = null;
                    bool? bt = true;
                    Ro? ro = new Ro(5);
                    s += (Show(q) + "|" + Show(r) + "|" + bq + "|" + bt + "|" + Show(ro?.Dbl()) + "|" + (nv?.Sum() == null) + "|" + (v?.Sum() > 2)) + "/";
                    var list = new List<int?> { 1, 2 };
                    s += (Show(list?.Count)) + "/";
                    return s;
                }
            }
            """, "T.test()");
        Assert.Equal("v=1|none|v=3|none|v=2|v=1/v=3|none|v=1|true|n|none|none|v=3|v=9|false/" +
            "v=4|v=9||true|v=10|true|true/v=2/", result);
    }
}
