namespace TinyCs.Tests;

public class StringTests
{
    [Fact]
    public void StringLiteral()
    {
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
            public class S
            {
                public static string Greet(string name, int age)
                {
                    return $"Hello {name}, age {age}";
                }
            }
            """,
            "S.Greet(\"Alice\", 30)");
        Assert.Equal("Hello Alice, age 30", result);
    }

    // Interpolated strings are emitted as `..` concatenation, but Lua's `#` binds tighter
    // than `..`, so parentheses are required in receiver position
    [Fact]
    public void InterpolationLengthBindsWholeString()
    {
        var result = TestHelper.CompileAndRunVm("""
            using TinySystem.Text;
            public class S
            {
                public static int Measure(int count)
                {
                    return $"n={count},tail".RuneCount();
                }
            }
            """,
            "S.Measure(2)");
        Assert.Equal("8", result);
    }

    [Fact]
    public void InterpolationLengthInArithmetic()
    {
        var result = TestHelper.CompileAndRunVm("""
            using TinySystem.Text;
            public class S
            {
                public static int Diff(int v)
                {
                    return v - $"i={v}".RuneCount();
                }
            }
            """,
            "S.Diff(100)");
        Assert.Equal("95", result);
    }

    [Fact]
    public void EmptyInterpolationIsEmptyString()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class S
            {
                public static string Tag(string s)
                {
                    return $"" + s + $"";
                }
            }
            """,
            "S.Tag(\"x\")");
        Assert.Equal("x", result);
    }

    [Fact]
    public void ConcatTreatsNullOperandAsEmptyString()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class S
            {
                public static string Wrap(string? s)
                {
                    return ">" + s + "<";
                }
            }
            """,
            "S.Wrap(null)");
        Assert.Equal("><", result);
    }

    [Fact]
    public void CompoundConcatTreatsNullAsEmptyString()
    {
        var result = TestHelper.CompileAndRunVm("""
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
            "S.Append(null, null)");
        Assert.Equal("-", result);
    }

    [Fact]
    public void HexEscapesKeepCsharpSemanticsInLua()
    {
        var result = TestHelper.CompileAndRunVm("""
            using TinySystem.Text;
            public class S
            {
                public static int Test()
                {
                    string good = "\x9Good text";
                    string bad = "\x9Bad text";
                    string zeroDigit = "\01";
                    return good.RuneCount() + bad.RuneCount() * 100
                        + zeroDigit.RuneCount() * 10000;
                }
            }
            """,
            "S.Test()");
        // "\x9Good..." = tab + "Good text" (10 chars), "\x9Bad..." = U+9BAD + " text" (6 chars), "\01" = NUL + '1' (2 chars)
        Assert.Equal((10 + 6 * 100 + 2 * 10000).ToString(), result);
    }

    [Fact]
    public void InterpolationAlignmentCaseAndEscapedBraces()
    {
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
            public class S
            {
                public static bool IsNull(string? s)
                {
                    return s == null;
                }
            }
            """,
            "S.IsNull(null)");
        Assert.Equal("True", result);
    }

    // #22: `+` with a bool operand stringifies with tostring, the same as ToString / interpolation
    // (Lua's `..` does not accept booleans)
    [Fact]
    public void ConcatBoolOperand()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class S
            {
                public static string Show(bool b) { return "b=" + b; }
            }
            """,
            "S.Show(true) + \",\" + S.Show(false)");
        Assert.Equal("b=True,b=False", result);
    }

    [Fact]
    public void ConcatBoolLeftOperandAndChain()
    {
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
            public class S
            {
                public static bool Same(bool b)
                {
                    return ("x" + b) == ("x" + b.ToString())
                        && ("x" + b) == $"x{b}";
                }
            }
            """,
            "S.Same(true) && S.Same(false)");
        Assert.Equal("True", result);
    }

    [Fact]
    public void CompoundConcatBoolOperand()
    {
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
            public class S
            {
                public static string Show(bool? b) { return "b=" + b; }
            }
            """,
            "S.Show(true) + \",\" + S.Show(false) + \",\" + S.Show(null)");
        Assert.Equal("b=True,b=False,b=", result);
    }

    // An operand whose static type is object is stringified by value (float is the same as .NET float.ToString(), null is an empty string)
    [Fact]
    public void ConcatObjectOperand()
    {
        var result = TestHelper.CompileAndRunVm("""
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

    // Interpolation and ToString() of object also stringify by value (null in interpolation is an empty string)
    [Fact]
    public void InterpolateAndToStringObject()
    {
        var result = TestHelper.CompileAndRunVm("""
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

    // Stringifying a class / record value stored in object is outside the subset (TCS1001 UnsupportedToString
    // only sees the static type). Lua faults instead of writing the table address
    [Fact]
    public void ObjectHoldingRecord_ToStringFaults()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TestHelper.CompileAndRunVm("""
            public record R(int A);
            public static class S
            {
                public static string Show() { object o = new R(1); return "x" + o; }
            }
            """, "S.Show()"));
        Assert.Contains("ToString of this value is not supported", ex.Message);
    }

    // Even if the value itself is the string "True", the dotnet differential compares it as a string
    [Fact]
    public void BoolToStringAsWholeResult()
    {
        var result = TestHelper.CompileAndRunVm("""
            public static class S
            {
                public static string Show() { bool b = true; return b.ToString(); }
            }
            """, "S.Show()");
        Assert.Equal("True", result);
    }
}
