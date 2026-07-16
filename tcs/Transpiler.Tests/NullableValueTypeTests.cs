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
            "T.Test()");
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
            "T.Test()");
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
            "tostring(T.Test())");
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
            "tostring(T.Test())");
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
            "T.Test()");
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
            "T.Test()");
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
            "T.Test()");
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
            "tostring(T.Test())");
        Assert.Equal("false", result);
    }

    // T146: Lua の `or` は false も fallback してしまうため、bool? の ?? は
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
            "T.Test()");
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
            "T.Test()");
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
            "T.Test()");
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
            "T.Test()");
        Assert.Equal("1|3", result);
    }
}
