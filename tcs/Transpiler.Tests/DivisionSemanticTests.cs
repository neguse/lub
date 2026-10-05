namespace TinyCs.Tests;

// C# の整数除算は 0 方向 truncation、剰余は被除数の符号。Lua の
// `/` (実数除算) と floor 由来 `%` をそのまま使うと負数と整数で結果がずれる。
public class DivisionSemanticTests
{
    [Fact]
    public void IntDivision_TruncatesTowardZero()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    var a = 5;
                    var b = 2;
                    var na = -5;
                    var nb = -2;
                    return $"{a / b}|{na / b}|{a / nb}|{na / nb}";
                }
            }
            """, "T.test()");
        Assert.Equal("2|-2|-2|2", result);
    }

    // (int)f は 0 方向 truncation (#13)。Lua 出力から cast が消えると float の
    // まま残る
    [Fact]
    public void FloatToIntCast_TruncatesTowardZero()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    float f = 2.7f;
                    float g = -2.7f;
                    double d = 9.99;
                    int a = (int)f;
                    int b = (int)g;
                    int c = (int)(f * 2f) + (int)d;
                    return $"{a}|{b}|{c}|{(int)(g / 2f)}";
                }
            }
            """, "T.test()");
        Assert.Equal("2|-2|14|-1", result);
    }

    [Fact]
    public void IntRemainder_TakesSignOfDividend()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    var a = 5;
                    var b = 2;
                    var na = -5;
                    var nb = -2;
                    return $"{a % b}|{na % b}|{a % nb}|{na % nb}";
                }
            }
            """, "T.test()");
        Assert.Equal("1|-1|1|-1", result);
    }

    [Fact]
    public void FloatDivision_KeepsIeeeSemantics()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    var a = 5.0f;
                    var b = 2.0f;
                    var zero = 0.0f;
                    var isInf = a / zero > 1e30f; // f32 数値モデルでも成立する閾値
                    return $"{a / b}|{isInf}";
                }
            }
            """, "T.test()");
        Assert.Equal("2.5|true", result);
    }

    [Fact]
    public void FloatRemainder_TruncatedLikeCsharp()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    var a = -5.5f;
                    var b = 2.0f;
                    return $"{a % b}";
                }
            }
            """, "T.test()");
        Assert.Equal("-1.5", result);
    }

    [Fact]
    public void CompoundDivideAndModulo_UseIntegerSemantics()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    var x = 7;
                    x /= 2;
                    var y = -7;
                    y %= 2;
                    return $"{x}|{y}";
                }
            }
            """, "T.test()");
        Assert.Equal("3|-1", result);
    }

    [Fact]
    public void IntDivisionByZero_RaisesRuntimeError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TestHelper.TranspileAndRunWithRuntime("""
                public class T
                {
                    public static int Test()
                    {
                        var a = 5;
                        var b = 0;
                        return a / b;
                    }
                }
                """, "T.test()"));
        Assert.Contains("Lua exited", ex.Message);
    }
}
