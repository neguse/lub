namespace TinyCs.Tests;

public class LambdaTests
{
    [Fact]
    public void SimpleLambda_Expression()
    {
        var result = TestHelper.TranspileAndRun("""
            using System;

            public class Fn
            {
                public static int Apply(Func<int, int> f, int x)
                {
                    return f(x);
                }

                public static int Test()
                {
                    return Apply(x => x * 2, 21);
                }
            }
            """,
            "Fn.Test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void ParenthesizedLambda()
    {
        var result = TestHelper.TranspileAndRun("""
            using System;

            public class Fn
            {
                public static int Apply(Func<int, int, int> f, int a, int b)
                {
                    return f(a, b);
                }

                public static int Test()
                {
                    return Apply((a, b) => a + b, 10, 32);
                }
            }
            """,
            "Fn.Test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void Lambda_AsVariable()
    {
        var result = TestHelper.TranspileAndRun("""
            using System;

            public class Fn
            {
                public static int Test()
                {
                    Func<int, int> doubler = x => x * 2;
                    return doubler(21);
                }
            }
            """,
            "Fn.Test()");
        Assert.Equal("42", result);
    }
}
