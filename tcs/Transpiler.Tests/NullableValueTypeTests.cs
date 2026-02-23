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
}
