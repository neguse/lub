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
            "S.Test()",
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

    // #22: bool operand の `+` は ToString / 補間と同じ tostring で文字列化する
    // (Lua の `..` は boolean を受けない)
    [Fact]
    public void ConcatBoolOperand()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static string Show(bool b) { return "b=" + b; }
            }
            """,
            "S.Show(true) .. ',' .. S.Show(false)");
        Assert.Equal("b=True,b=False", result);
    }

    [Fact]
    public void ConcatBoolLeftOperandAndChain()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static string Show(bool b, int n)
                {
                    return b + ":" + n + ":" + !b;
                }
            }
            """,
            "S.Show(true, 3)");
        Assert.Equal("True:3:False", result);
    }

    [Fact]
    public void ConcatBoolMatchesToString()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static bool Same(bool b)
                {
                    return ("x" + b) == ("x" + b.ToString())
                        && ("x" + b) == $"x{b}";
                }
            }
            """,
            "tostring(S.Same(true) and S.Same(false))");
        Assert.Equal("true", result);
    }

    [Fact]
    public void CompoundConcatBoolOperand()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static string Show(bool b)
                {
                    string s = "b=";
                    s += b;
                    s += "/";
                    s += !b;
                    return s;
                }
            }
            """,
            "S.Show(true)");
        Assert.Equal("b=True/False", result);
    }

    [Fact]
    public void ConcatNullableBoolOperand()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static string Show(bool? b) { return "b=" + b; }
            }
            """,
            "S.Show(true) .. ',' .. S.Show(false) .. ',' .. S.Show(nil)");
        Assert.Equal("b=True,b=False,b=", result);
    }

    // 静的型が object の operand は値で文字列化する (float は .NET の float.ToString() と同じ、null は空文字列)
    [Fact]
    public void ConcatObjectOperand()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static string Show()
                {
                    object f = 1000000f;
                    object i = 7;
                    object? n = null;
                    string s = "f=" + f;
                    s += f;
                    return s + "," + (f + "!") + ",i=" + i + n + "|";
                }
            }
            """,
            "S.Show()");
        Assert.Equal("f=10000001000000,1000000!,i=7|", result);
    }

    // object の補間と ToString() も値で文字列化する (補間の null は空文字列)
    [Fact]
    public void InterpolateAndToStringObject()
    {
        var result = TestHelper.TranspileAndRun("""
            public class S
            {
                public static string Show()
                {
                    object f = 1000000f;
                    object g = 2f / 3f;
                    object? n = null;
                    object i = 7;
                    return $"{f},{g},{n},{i}" + "|" + f.ToString() + "|" + g.ToString() + "|" + $"[{f,10}]";
                }
            }
            """,
            "S.Show()");
        Assert.Equal("1000000,0.6666667,,7|1000000|0.6666667|[   1000000]", result);
    }

    // object に入れた class / record の値の文字列化はサブセット外 (TCS1001 UnsupportedToString は
    // 静的型でしか見えない)。Lua は table のアドレスを書かずに fault する
    [Fact]
    public void ObjectHoldingRecord_ToStringFaults()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TestHelper.TranspileAndRun("""
            public record R(int A);
            public static class S
            {
                public static string Show() { object o = new R(1); return "x" + o; }
            }
            """, "S.Show()", differential: false));
        Assert.Contains("ToString of this value is not supported", ex.Message);
    }

    // 値そのものが "True" の文字列でも、dotnet differential は文字列として比べる
    [Fact]
    public void BoolToStringAsWholeResult()
    {
        var result = TestHelper.TranspileAndRun("""
            public static class S
            {
                public static string Show() { bool b = true; return b.ToString(); }
            }
            """, "S.Show()");
        Assert.Equal("True", result);
    }
}
