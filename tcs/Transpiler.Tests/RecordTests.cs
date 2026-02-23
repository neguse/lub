namespace TinyCs.Tests;

public class RecordTests
{
    [Fact]
    public void Record_Positional_Create()
    {
        var result = TestHelper.TranspileAndRun("""
            public record Point(int X, int Y);
            public class T
            {
                public static int Test()
                {
                    var p = new Point(3, 4);
                    return p.X + p.Y;
                }
            }
            """, "T.Test()");
        Assert.Equal("7", result);
    }

    [Fact]
    public void Record_Positional_MultipleInstances()
    {
        var result = TestHelper.TranspileAndRun("""
            public record Vec2(int X, int Y);
            public class T
            {
                public static int Test()
                {
                    var a = new Vec2(1, 2);
                    var b = new Vec2(10, 20);
                    return a.X + b.Y;
                }
            }
            """, "T.Test()");
        Assert.Equal("21", result);
    }
}
