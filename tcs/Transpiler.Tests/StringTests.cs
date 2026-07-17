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
    public void HexEscapesKeepCsharpSemanticsInLua()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static int Test()
                {
                    string good = "\x9Good text";
                    string bad = "\x9Bad text";
                    string zeroDigit = "\01";
                    return good.Length + bad.Length * 100
                        + zeroDigit.Length * 10000;
                }
            }
            """,
            "S.Test()");
        // C#: "\x9Good..." = tab + "Good text" (10 chars)、
        //     "\x9Bad..." = U+9BAD + " text" (6 chars → UTF-8 では 3+5 bytes)、
        //     "\01" = NUL + '1' (2 chars)。Lua 側は byte 長。
        Assert.Equal((10 + 8 * 100 + 2 * 10000).ToString(), result);
    }

    [Fact]
    public void InterpolationAlignmentCaseAndEscapedBraces()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static string Test()
                {
                    int val = 255;
                    return $"{{x={val,6:X}}} {val:x} {val,-4}|";
                }
            }
            """,
            "S.Test()");
        Assert.Equal("{x=    FF} ff 255 |", result);
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
