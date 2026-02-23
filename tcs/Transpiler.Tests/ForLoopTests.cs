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
}
