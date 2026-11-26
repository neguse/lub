namespace TinyCs.Tests;

// C# integer division truncates toward 0, and the remainder has the sign of the dividend. Using Lua's
// `/` (real division) and floor-based `%` as is gives different results for negative numbers and integers.
public class DivisionSemanticTests
{
    [Fact]
    public void IntDivision_TruncatesTowardZero()
    {
        var result = TestHelper.CompileAndRunVm("""
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
            """, "T.Test()");
        Assert.Equal("2|-2|-2|2", result);
    }

    // (int)f truncates toward 0 (#13). If the cast disappears from the Lua output, the value stays
    // a float
    [Fact]
    public void FloatToIntCast_TruncatesTowardZero()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static string Test()
                {
                    float f = 2.7f;
                    float g = -2.7f;
                    float d = 9.99f;
                    int a = (int)f;
                    int b = (int)g;
                    int c = (int)(f * 2f) + (int)d;
                    return $"{a}|{b}|{c}|{(int)(g / 2f)}";
                }
            }
            """, "T.Test()");
        Assert.Equal("2|-2|14|-1", result);
    }

    [Fact]
    public void IntRemainder_TakesSignOfDividend()
    {
        var result = TestHelper.CompileAndRunVm("""
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
            """, "T.Test()");
        Assert.Equal("1|-1|1|-1", result);
    }

    [Fact]
    public void FloatDivision_KeepsIeeeSemantics()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static string Test()
                {
                    var a = 5.0f;
                    var b = 2.0f;
                    var zero = 0.0f;
                    var isInf = a / zero > 1e30f; // A threshold that holds even in the f32 numeric model
                    return $"{a / b}|{isInf}";
                }
            }
            """, "T.Test()");
        Assert.Equal("2.5|True", result);
    }

    [Fact]
    public void FloatRemainder_TruncatedLikeCsharp()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static string Test()
                {
                    var a = -5.5f;
                    var b = 2.0f;
                    return $"{a % b}";
                }
            }
            """, "T.Test()");
        Assert.Equal("-1.5", result);
    }

    [Fact]
    public void CompoundDivideAndModulo_UseIntegerSemantics()
    {
        var result = TestHelper.CompileAndRunVm("""
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
            """, "T.Test()");
        Assert.Equal("3|-1", result);
    }

    [Fact]
    public void IntDivisionByZero_RaisesRuntimeError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TestHelper.CompileAndRunVm("""
                public class T
                {
                    public static int Test()
                    {
                        var a = 5;
                        var b = 0;
                        return a / b;
                    }
                }
                """, "T.Test()"));
        Assert.Contains("fault: division by zero", ex.Message);
    }
}
