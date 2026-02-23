namespace TinyCs.Tests;

public class TernaryTests
{
    [Fact]
    public void Ternary_TrueBranch()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Max(int a, int b)
                {
                    return a > b ? a : b;
                }
            }
            """,
            "T.Max(10, 5)");
        Assert.Equal("10", result);
    }

    [Fact]
    public void Ternary_FalseBranch()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Max(int a, int b)
                {
                    return a > b ? a : b;
                }
            }
            """,
            "T.Max(3, 8)");
        Assert.Equal("8", result);
    }

    [Fact]
    public void Ternary_FalsyTrueValue()
    {
        // When true-branch is 0 (falsy in Lua), the IIFE approach is needed
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Pick(bool cond)
                {
                    return cond ? 0 : 99;
                }
            }
            """,
            "T.Pick(true)");
        Assert.Equal("0", result);
    }
}
