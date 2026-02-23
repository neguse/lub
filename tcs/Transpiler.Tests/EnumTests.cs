namespace TinyCs.Tests;

public class EnumTests
{
    [Fact]
    public void BasicEnum()
    {
        var result = TestHelper.TranspileAndRun("""
            public enum Direction { Up, Down, Left, Right }

            public class Nav
            {
                public static int GetDir() { return Direction.Right; }
            }
            """,
            "Nav.GetDir()");
        Assert.Equal("3", result);
    }

    [Fact]
    public void EnumWithExplicitValues()
    {
        var result = TestHelper.TranspileAndRun("""
            public enum Status { Idle = 0, Walking = 10, Running = 20 }

            public class Game
            {
                public static int Speed(int status)
                {
                    if (status == Status.Idle) { return 0; }
                    else if (status == Status.Walking) { return 10; }
                    else { return 20; }
                }
            }
            """,
            "Game.Speed(10)");
        Assert.Equal("10", result);
    }

    [Fact]
    public void EnumComparison()
    {
        var result = TestHelper.TranspileAndRun("""
            public enum State { Idle, Active, Done }

            public class Machine
            {
                public int Current = 0;

                public void Activate()
                {
                    this.Current = State.Active;
                }

                public bool IsActive()
                {
                    return this.Current == State.Active;
                }
            }
            """, """
            (function()
              local m = Machine.new()
              m:Activate()
              return tostring(m:IsActive())
            end)()
            """);
        Assert.Equal("true", result);
    }
}
