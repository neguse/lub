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
            "S.hello()");
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
            "S.greet('Alice', 30)");
        Assert.Equal("Hello Alice, age 30", result);
    }

    // 補間文字列は `..` 連接で emit されるが、Lua の `#` は `..` より強く
    // 結合するため、receiver 位置では括弧が必須
    [Fact]
    public void InterpolationLengthBindsWholeString()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static int Measure(int count)
                {
                    return $"n={count},tail".Length;
                }
            }
            """,
            "S.Measure(2)");
        Assert.Equal("8", result);
    }

    [Fact]
    public void InterpolationLengthInArithmetic()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static int Diff(int v)
                {
                    return v - $"i={v}".Length;
                }
            }
            """,
            "S.Diff(100)");
        Assert.Equal("95", result);
    }

    [Fact]
    public void EmptyInterpolationIsEmptyString()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static string Tag(string s)
                {
                    return $"" + s + $"";
                }
            }
            """,
            "S.Tag('x')");
        Assert.Equal("x", result);
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
            "S.wrap(nil)");
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
            "S.append(nil, nil)");
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
            "S.test()",
            differential: false); // Length は C# UTF-16 / Lua byte の既知差異
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
            "S.test()");
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
            "tostring(S.is_null(nil))");
        Assert.Equal("true", result);
    }
}
