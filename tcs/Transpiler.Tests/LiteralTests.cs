namespace TinyCs.Tests;

public class LiteralTests
{
    // T74: Hex literals
    [Fact]
    public void HexLiteral_0xFF()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test() => 0xFF;
            }
            """, "T.Test()");
        Assert.Equal("255", result);
    }

    [Fact]
    public void HexLiteral_0x1A()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test() => 0x1A;
            }
            """, "T.Test()");
        Assert.Equal("26", result);
    }

    // T75: Digit separators
    [Fact]
    public void DigitSeparator_Int()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test() => 1_000_000;
            }
            """, "T.Test()");
        Assert.Equal("1000000", result);
    }

    [Fact]
    public void DigitSeparator_Hex()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test() => 0xFF_FF;
            }
            """, "T.Test()");
        Assert.Equal("65535", result);
    }

    // T76: Character literals
    [Fact]
    public void CharLiteral_Basic()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    char c = 'A';
                    return c.ToString();
                }
            }
            """, "T.Test()");
        Assert.Equal("A", result);
    }

    // T77: Verbatim string literals
    [Fact]
    public void VerbatimString_Backslash()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test() => @"C:\Users\test";
            }
            """, "T.Test()");
        Assert.Equal(@"C:\Users\test", result);
    }

    // T78: Raw string literals
    [Fact]
    public void RawString_Simple()
    {
        var result = TestHelper.TranspileAndRun(""""
            public class T
            {
                public static string Test() => """hello world""";
            }
            """", "T.Test()");
        Assert.Equal("hello world", result);
    }

    // T79: Binary literals
    [Fact]
    public void BinaryLiteral_0b1010()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test() => 0b1010;
            }
            """, "T.Test()");
        Assert.Equal("10", result);
    }

    [Fact]
    public void BinaryLiteral_WithSeparator()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test() => 0b1111_0000;
            }
            """, "T.Test()");
        Assert.Equal("240", result);
    }

    // T80: default / default(T)
    [Fact]
    public void Default_Int()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    int x = default;
                    return x;
                }
            }
            """, "T.Test()");
        Assert.Equal("0", result);
    }

    [Fact]
    public void Default_Bool()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static bool Test()
                {
                    bool b = default;
                    return b;
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("false", result);
    }

    [Fact]
    public void DefaultT_String()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    string s = default(string);
                    return s == null ? "nil" : s;
                }
            }
            """, "T.Test()");
        Assert.Equal("nil", result);
    }
}
