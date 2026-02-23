namespace TinyCs.Tests;

public class NullConditionalTests
{
    [Fact]
    public void NullConditional_PropertyAccess_NotNull()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Obj
            {
                public int Value = 42;
            }
            public class T
            {
                public static int Test()
                {
                    Obj o = new Obj();
                    var v = o?.Value;
                    return v ?? 0;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void NullConditional_PropertyAccess_Null()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Obj
            {
                public int Value = 42;
            }
            public class T
            {
                public static string Test()
                {
                    Obj o = null;
                    var v = o?.Value;
                    return v == null ? "nil" : "not nil";
                }
            }
            """,
            "T.Test()");
        Assert.Equal("nil", result);
    }

    [Fact]
    public void IsPattern_NotNull()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static bool Test()
                {
                    string s = "hello";
                    return s is not null;
                }
            }
            """,
            "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void IsPattern_Null()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static bool Test()
                {
                    string s = null;
                    return s is null;
                }
            }
            """,
            "tostring(T.Test())");
        Assert.Equal("true", result);
    }
}
