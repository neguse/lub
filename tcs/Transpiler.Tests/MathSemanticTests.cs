namespace TinyCs.Tests;

public class MathSemanticTests
{
    [Fact]
    public void Math_Min()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static int Test() => Math.Min(3, 7);
            }
            """, "T.Test()");
        Assert.Equal("3", result);
    }

    [Fact]
    public void Math_Max()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static int Test() => Math.Max(3, 7);
            }
            """, "T.Test()");
        Assert.Equal("7", result);
    }

    [Fact]
    public void Math_Abs()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static int Test() => Math.Abs(-42);
            }
            """, "T.Test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void Math_Floor()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static double Test() => Math.Floor(3.7);
            }
            """, "T.Test()");
        Assert.Equal("3", result);
    }

    [Fact]
    public void Math_Ceiling()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static double Test() => Math.Ceiling(3.2);
            }
            """, "T.Test()");
        Assert.Equal("4", result);
    }

    [Fact]
    public void Math_Sin()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static double Test() => Math.Sin(0);
            }
            """, "T.Test()");
        Assert.Equal("0.0", result);
    }

    [Fact]
    public void Math_Cos()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static double Test() => Math.Cos(0);
            }
            """, "T.Test()");
        Assert.Equal("1.0", result);
    }

    [Fact]
    public void Math_Atan2()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static double Test() => Math.Atan2(0, 1);
            }
            """, "T.Test()");
        Assert.Equal("0.0", result);
    }

    [Fact]
    public void Math_PI()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static bool Test() => Math.PI > 3.14 && Math.PI < 3.15;
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void Math_Pow()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static double Test() => Math.Pow(2, 5);
            }
            """, "T.Test()");
        Assert.Equal("32.0", result);
    }
}
