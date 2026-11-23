namespace TinyCs.Tests;

// Pins down the C# semantics of integer bitwise operators and shifts (count & 31, >> is arithmetic).
// The width is i32 (see support-matrix).
public class BitwiseOperatorTests
{
    [Fact]
    public void BitwiseAnd()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test() => 5 & 3;
            }
            """, "T.Test()");
        Assert.Equal("1", result);
    }

    [Fact]
    public void BitwiseOr()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test() => 5 | 2;
            }
            """, "T.Test()");
        Assert.Equal("7", result);
    }

    [Fact]
    public void BitwiseXor()
    {
        // C#'s ^ is Lua 5.5's binary ~ (Lua's ^ is exponentiation)
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test() => 5 ^ 3;
            }
            """, "T.Test()");
        Assert.Equal("6", result);
    }

    [Fact]
    public void BitwiseNot()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test() => ~5;
            }
            """, "T.Test()");
        Assert.Equal("-6", result);
    }

    [Fact]
    public void ShiftLeft()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test() => 1 << 4;
            }
            """, "T.Test()");
        Assert.Equal("16", result);
    }

    [Fact]
    public void ShiftRight()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test() => 256 >> 4;
            }
            """, "T.Test()");
        Assert.Equal("16", result);
    }

    [Fact]
    public void Precedence_MatchesCSharp()
    {
        // C#: & binds tighter than | → 1 | (2 & 3) = 3. Lua has the same relative precedence
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test() => 1 | 2 & 3;
            }
            """, "T.Test()");
        Assert.Equal("3", result);
    }

    [Fact]
    public void ShiftWithAdditiveOperand()
    {
        // C#: addition binds tighter than shift → 1 << (2 + 1) = 8
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test(int n) => 1 << n + 1;
            }
            """, "T.Test(2)");
        Assert.Equal("8", result);
    }

    [Fact]
    public void CompoundAssignments()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test()
                {
                    var x = 12;
                    x &= 10;   // 8
                    x |= 3;    // 11
                    x ^= 1;    // 10
                    x <<= 2;   // 40
                    x >>= 3;   // 5
                    return x;
                }
            }
            """, "T.Test()");
        Assert.Equal("5", result);
    }

    [Fact]
    public void MaskCheck_InCondition()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test()
                {
                    var flags = 6;
                    if ((flags & 2) != 0) return 1;
                    return 0;
                }
            }
            """, "T.Test()");
        Assert.Equal("1", result);
    }

    [Fact]
    public void FlagsEnum_BitwiseOps()
    {
        var result = TestHelper.CompileAndRunVm("""
            public enum Layer
            {
                None = 0,
                Player = 1,
                Enemy = 2,
                Wall = 4,
            }

            public class T
            {
                public static int Test()
                {
                    var mask = Layer.Player | Layer.Wall;
                    var cleared = mask & ~Layer.Player;
                    return (int)cleared;
                }
            }
            """, "T.Test()");
        Assert.Equal("4", result);
    }

    [Fact]
    public void BoolBitwiseOperators_ReportUnsupported()
    {
        // Lua's & cannot be applied to booleans, and mapping to and/or short-circuits, which
        // changes the side-effect semantics of C#'s non-short-circuit & |, so it is unsupported (error)
        var result = Checker.Check(["""
            public class T
            {
                public static bool Test(bool a, bool b) => a & b;
            }
            """]);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e =>
            e.Contains(TinyCsDiagnosticIds.UnsupportedSyntax)
            && e.Contains("BitwiseAndExpression"));
    }

    // C#'s int >> is an arithmetic shift (sign-extending), and count is masked with 31. Matches real .NET
    [Fact]
    public void Shift_NegativeRightShiftIsArithmetic_AndCountIsMasked()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static string Test()
                {
                    int a = -8;
                    int b = -1;
                    int c = 1;
                    int n = 33;
                    int m = -1;
                    int x = -1024;
                    x >>= 2;
                    int y = 3;
                    y <<= 34;
                    int? q = -16;
                    int? r = q >> 2;
                    return (a >> 1) + "|" + (b >> 31) + "|" + (c << n) + "|" + (a >> n) + "|" + (c << m)
                        + "|" + (b >> 0) + "|" + x + "|" + y + "|" + (-2147483648 >> 31) + "|" + r;
                }
            }
            """, "T.Test()");
        Assert.Equal("-4|-1|2|-4|-2147483648|-1|-256|12|-1|-4", result);
    }

    // `-2147483648` / int.MinValue stays an integer in arithmetic and display (matches real .NET)
    [Fact]
    public void IntMinValue_LiteralStaysInteger()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public const int Floor = -2147483648;
                public static string Test()
                {
                    int a = -2147483648;
                    int b = int.MinValue;
                    int mx = 2147483647;
                    return a + "|" + b + "|" + Floor + "|" + (a == b) + "|" + (a >> 31) + "|" + (a & 0x7FFFFFFF) + "|" + (mx + 1 == a);
                }
            }
            """, "T.Test()");
        Assert.Equal("-2147483648|-2147483648|-2147483648|True|-1|0|True", result);
    }

    // The right-hand side of `x op= a ⊕ b` is one term (Lua's xor / | / shift bind weaker than +, so
    // without parentheses `x + a ~ b` turns into `(x + a) ~ b`)
    [Fact]
    public void CompoundAssignment_RightOperandIsOneTerm()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static string Test()
                {
                    int s = 100; s += 6 ^ 3;
                    int m = 2; m *= 3 + 4;
                    int d = 50; d -= 20 - 5;
                    int o = 1; o |= 6 & 3;
                    int h = 1; h <<= 1 + 1;
                    int q = 7; q &= 5 | 2;
                    int x = 3; x ^= 1 << 2;
                    return s + "|" + m + "|" + d + "|" + o + "|" + h + "|" + q + "|" + x;
                }
            }
            """, "T.Test()");
        Assert.Equal("105|14|35|3|4|7|7", result);
    }
}
