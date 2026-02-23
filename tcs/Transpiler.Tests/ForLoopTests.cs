namespace TinyCs.Tests;

public class ForLoopTests
{
    [Fact]
    public void SimpleFor_Sum()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Loop
            {
                public static int Sum(int n)
                {
                    var total = 0;
                    for (int i = 0; i < n; i++)
                    {
                        total = total + i;
                    }
                    return total;
                }
            }
            """,
            "Loop.Sum(5)");
        Assert.Equal("10", result);
    }

    [Fact]
    public void SimpleFor_StartFromOne()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Loop
            {
                public static int Sum(int n)
                {
                    var total = 0;
                    for (int i = 1; i <= n; i++)
                    {
                        total = total + i;
                    }
                    return total;
                }
            }
            """,
            "Loop.Sum(10)");
        Assert.Equal("55", result);
    }

    [Fact]
    public void For_WithBreak()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Loop
            {
                public static int FirstOver(int limit)
                {
                    for (int i = 0; i < 100; i++)
                    {
                        if (i > limit) { return i; }
                    }
                    return -1;
                }
            }
            """,
            "Loop.FirstOver(5)");
        Assert.Equal("6", result);
    }

    [Fact]
    public void For_Nested()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Loop
            {
                public static int Multiply(int rows, int cols)
                {
                    var count = 0;
                    for (int r = 0; r < rows; r++)
                    {
                        for (int c = 0; c < cols; c++)
                        {
                            count = count + 1;
                        }
                    }
                    return count;
                }
            }
            """,
            "Loop.Multiply(3, 4)");
        Assert.Equal("12", result);
    }

    // ===== continue =====

    [Fact]
    public void Continue_ForLoop()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    int sum = 0;
                    for (int i = 0; i < 10; i++)
                    {
                        if (i % 2 == 0) continue;
                        sum = sum + i;
                    }
                    return sum;
                }
            }
            """, "T.Test()");
        Assert.Equal("25", result); // 1+3+5+7+9
    }

    [Fact]
    public void Continue_WhileLoop()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    int sum = 0;
                    int i = 0;
                    while (i < 5)
                    {
                        i = i + 1;
                        if (i == 3) continue;
                        sum = sum + i;
                    }
                    return sum;
                }
            }
            """, "T.Test()");
        Assert.Equal("12", result); // 1+2+4+5
    }

    [Fact]
    public void Continue_ForEach()
    {
        var result = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 1, 2, 3, 4, 5 };
                    int sum = 0;
                    foreach (var x in list)
                    {
                        if (x == 3) continue;
                        sum = sum + x;
                    }
                    return sum;
                }
            }
            """, "T.Test()");
        Assert.Equal("12", result); // 1+2+4+5
    }

    // ===== do-while =====

    [Fact]
    public void DoWhile_Basic()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    int i = 0;
                    do
                    {
                        i = i + 1;
                    } while (i < 5);
                    return i;
                }
            }
            """, "T.Test()");
        Assert.Equal("5", result);
    }

    [Fact]
    public void DoWhile_ExecutesAtLeastOnce()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    int i = 10;
                    do
                    {
                        i = i + 1;
                    } while (i < 5);
                    return i;
                }
            }
            """, "T.Test()");
        Assert.Equal("11", result); // Executes once even though 10 >= 5
    }
}
