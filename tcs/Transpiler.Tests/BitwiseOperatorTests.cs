namespace TinyCs.Tests;

// 整数ビット演算子の Lua 5.5 native 演算子 (& | ~ ~ << >>) への写像を固定する。
// C# int は 32bit / Lua 整数は 64bit のため、幅に依存する結果 (負数シフト、
// 上位 bit の折り返し) は移植側の明示マスク運用 (support-matrix 参照)。
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
            """, "T.Test()");
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
            """, "T.Test()");
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
            """, "T.Test()");
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
            """, "T.Test()");
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
            """, "T.Test()");
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
            """, "T.Test()");
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
            """, "T.Test()");
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
            """, "T.Test(2)");
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
            """, "T.Test()");
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
            """, "T.Test()");
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
            """, "T.Test()");
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
}
