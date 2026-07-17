namespace TinyCs.Tests;

public class StringTests
{
    [Fact]
    public void StringLiteral()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static string Hello() { return "world"; }
            }
            """,
            "S.Hello()");
        Assert.Equal("world", result);
    }

    [Fact]
    public void StringInterpolation()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static string Greet(string name, int age)
                {
                    return $"Hello {name}, age {age}";
                }
            }
            """,
            "S.Greet('Alice', 30)");
        Assert.Equal("Hello Alice, age 30", result);
    }

    [Fact]
    public void ConcatTreatsNullOperandAsEmptyString()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static string Wrap(string? s)
                {
                    return ">" + s + "<";
                }
            }
            """,
            "S.Wrap(nil)");
        Assert.Equal("><", result);
    }

    [Fact]
    public void CompoundConcatTreatsNullAsEmptyString()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static string Append(string? head, string? tail)
                {
                    string? acc = head;
                    acc += "-";
                    acc += tail;
                    return acc;
                }
            }
            """,
            "S.Append(nil, nil)");
        Assert.Equal("-", result);
    }

    [Fact]
    public void NullLiteral()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static bool IsNull(string? s)
                {
                    return s == null;
                }
            }
            """,
            "tostring(S.IsNull(nil))");
        Assert.Equal("true", result);
    }
}
