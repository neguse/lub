namespace TinyCs.Tests;

// 整数ビット演算子の Lua 5.5 native 演算子 (& | ~ ~) への写像と、シフトの
// C# 意味論 helper (__tcs_shl / __tcs_shr: count & 31、>> は算術) を固定する。
// 幅は LUA_32BITS 構成の Lua (lua32) を数値基準とする (support-matrix 参照)。
public class BitwiseOperatorTests
{
    [Fact]
    public void BitwiseAnd()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test() => 5 & 3;
            }
            """, "T.test()");
        Assert.Equal("1", result);
    }

    [Fact]
    public void BitwiseOr()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test() => 5 | 2;
            }
            """, "T.test()");
        Assert.Equal("7", result);
    }

    [Fact]
    public void BitwiseXor()
    {
        // C# の ^ は Lua 5.5 の二項 ~ (Lua の ^ は冪乗)
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test() => 5 ^ 3;
            }
            """, "T.test()");
        Assert.Equal("6", result);
    }

    [Fact]
    public void BitwiseNot()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test() => ~5;
            }
            """, "T.test()");
        Assert.Equal("-6", result);
    }

    [Fact]
    public void ShiftLeft()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test() => 1 << 4;
            }
            """, "T.test()");
        Assert.Equal("16", result);
    }

    [Fact]
    public void ShiftRight()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test() => 256 >> 4;
            }
            """, "T.test()");
        Assert.Equal("16", result);
    }

    [Fact]
    public void Precedence_MatchesCSharp()
    {
        // C#: & が | より強い → 1 | (2 & 3) = 3。Lua も同じ相対順位
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test() => 1 | 2 & 3;
            }
            """, "T.test()");
        Assert.Equal("3", result);
    }

    [Fact]
    public void ShiftWithAdditiveOperand()
    {
        // C#: 加算がシフトより強い → 1 << (2 + 1) = 8
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test(int n) => 1 << n + 1;
            }
            """, "T.test(2)");
        Assert.Equal("8", result);
    }

    [Fact]
    public void CompoundAssignments()
    {
        var result = TestHelper.TranspileAndRun("""
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
            """, "T.test()");
        Assert.Equal("5", result);
    }

    [Fact]
    public void MaskCheck_InCondition()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    var flags = 6;
                    if ((flags & 2) != 0) return 1;
                    return 0;
                }
            }
            """, "T.test()");
        Assert.Equal("1", result);
    }

    [Fact]
    public void FlagsEnum_BitwiseOps()
    {
        var result = TestHelper.TranspileAndRun("""
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
            """, "T.test()");
        Assert.Equal("4", result);
    }

    [Fact]
    public void BoolBitwiseOperators_ReportUnsupported()
    {
        // Lua の & は boolean に適用できず、and/or への写像は短絡評価で
        // C# の非短絡 & | と副作用意味論が変わるため未対応警告にする
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static bool Test(bool a, bool b) => a & b;
            }
            """]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Contains(result.Warnings, w =>
            w.Contains(TinyCsDiagnosticIds.UnsupportedSyntax)
            && w.Contains("BitwiseAndExpression"));
    }

    // C# の int >> は算術シフト (符号拡張)、count は 31 でマスク。実 .NET と一致
    [Fact]
    public void Shift_NegativeRightShiftIsArithmetic_AndCountIsMasked()
    {
        var result = TestHelper.TranspileAndRun("""
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
            """, "T.test()");
        Assert.Equal("-4|-1|2|-4|-2147483648|-1|-256|12|-1|-4", result);
    }

    // `-2147483648` / int.MinValue は lua32 で literal 2147483648 が float に
    // なるため式形で出す。整数のまま演算・表示されること (実 .NET と一致)
    [Fact]
    public void IntMinValue_LiteralStaysInteger()
    {
        var result = TestHelper.TranspileAndRun("""
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
            """, "T.test()");
        Assert.Equal("-2147483648|-2147483648|-2147483648|true|-1|0|true", result);
    }

    // `x op= a ⊕ b` の右辺は 1 項 (Lua の xor / | / shift は + より弱いので、
    // 括らないと `x + a ~ b` が `(x + a) ~ b` に化ける)
    [Fact]
    public void CompoundAssignment_RightOperandIsOneTerm()
    {
        var result = TestHelper.TranspileAndRun("""
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
            """, "T.test()");
        Assert.Equal("105|14|35|3|4|7|7", result);
    }
}
