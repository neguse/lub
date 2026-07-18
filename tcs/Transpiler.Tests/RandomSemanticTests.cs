namespace TinyCs.Tests;

public class RandomSemanticTests
{
    [Fact]
    public void Random_NextFloat_InRange()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test()
                {
                    var f = TinySystem.Random.NextFloat();
                    return f >= 0.0f && f < 1.0f;
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void Random_Next_InRange()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test()
                {
                    var n = TinySystem.Random.Next(10);
                    return n >= 0 && n < 10;
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void Random_Range_InRange()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test()
                {
                    var n = TinySystem.Random.Range(5, 10);
                    return n >= 5 && n <= 10;
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }
}
