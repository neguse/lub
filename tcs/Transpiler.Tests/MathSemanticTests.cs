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
            """, "T.test()");
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
            """, "T.test()");
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
            """, "T.test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void Math_Floor()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static float Test() => (float)Math.Floor(3.7f);
            }
            """, "T.test()");
        Assert.Equal("3", result);
    }

    [Fact]
    public void Math_Ceiling()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static float Test() => (float)Math.Ceiling(3.2f);
            }
            """, "T.test()");
        Assert.Equal("4", result);
    }

    [Fact]
    public void Math_Sin()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static float Test() => (float)Math.Sin(0);
            }
            """, "T.test()");
        Assert.Equal("0.0", result);
    }

    [Fact]
    public void Math_Cos()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static float Test() => (float)Math.Cos(0);
            }
            """, "T.test()");
        Assert.Equal("1.0", result);
    }

    [Fact]
    public void Math_Atan2()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static float Test() => (float)Math.Atan2(0, 1);
            }
            """, "T.test()");
        Assert.Equal("0.0", result);
    }

    [Fact]
    public void Math_PI()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static bool Test() => Math.PI > 3.14f && Math.PI < 3.15f;
            }
            """, "tostring(T.test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void Math_Pow()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static float Test() => (float)Math.Pow(2, 5);
            }
            """, "T.test()");
        Assert.Equal("32.0", result);
    }
}
