namespace TinyCs.Tests;

public class SwitchTests
{
    [Fact]
    public void SwitchStatement_Basic()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    int x = 2;
                    string result = "";
                    switch (x)
                    {
                        case 1:
                            result = "one";
                            break;
                        case 2:
                            result = "two";
                            break;
                        default:
                            result = "other";
                            break;
                    }
                    return result;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("two", result);
    }

    [Fact]
    public void SwitchStatement_Default()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    int x = 99;
                    switch (x)
                    {
                        case 1:
                            return "one";
                        case 2:
                            return "two";
                        default:
                            return "other";
                    }
                }
            }
            """,
            "T.Test()");
        Assert.Equal("other", result);
    }

    [Fact]
    public void SwitchStatement_WithEnum()
    {
        var result = TestHelper.TranspileAndRun("""
            public enum Dir { Up = 0, Down = 1, Left = 2, Right = 3 }
            public class T
            {
                public static string Test()
                {
                    Dir d = Dir.Left;
                    switch (d)
                    {
                        case Dir.Up: return "up";
                        case Dir.Down: return "down";
                        case Dir.Left: return "left";
                        case Dir.Right: return "right";
                        default: return "?";
                    }
                }
            }
            """,
            "T.Test()");
        Assert.Equal("left", result);
    }

    [Fact]
    public void SwitchExpression_Basic()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    int x = 3;
                    var msg = x switch
                    {
                        1 => "one",
                        2 => "two",
                        3 => "three",
                        _ => "other"
                    };
                    return msg;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("three", result);
    }

    [Fact]
    public void SwitchExpression_WithEnum()
    {
        var result = TestHelper.TranspileAndRun("""
            public enum State { Idle = 0, Running = 1, Done = 2 }
            public class T
            {
                public static string Test()
                {
                    State s = State.Running;
                    var name = s switch
                    {
                        State.Idle => "idle",
                        State.Running => "running",
                        State.Done => "done",
                        _ => "unknown"
                    };
                    return name;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("running", result);
    }
}
