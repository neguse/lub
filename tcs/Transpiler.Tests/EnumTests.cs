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
            "Nav.get_dir()");
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
            "Game.speed(10)");
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
              m:activate()
              return tostring(m:is_active())
            end)()
            """);
        Assert.Equal("true", result);
    }

    [Fact]
    public void IntegerToEnum_AndNotEquals_AreAllowed()
    {
        var result = TestHelper.TranspileAndRun("""
            public enum State { Idle, Active, Done }

            public class T
            {
                public static bool Test()
                {
                    State state = 1;
                    return state != 0;
                }
            }
            """, "tostring(T.test())");

        Assert.Equal("true", result);
    }

    // #14: initializer 無しの enum 型 field / property の既定値は 0 (default(E))
    [Fact]
    public void EnumField_DefaultIsZero()
    {
        var result = TestHelper.TranspileAndRun("""
            public enum State { Idle, Run }
            public class Actor
            {
                public State S;
                public static State Global;
                public State P { get; set; }
            }
            public static class T
            {
                public static bool Test()
                {
                    var a = new Actor();
                    return a.S == State.Idle && Actor.Global == State.Idle
                        && a.P == State.Idle && default(State) == State.Idle;
                }
            }
            """, "tostring(T.Test())");

        Assert.Equal("true", result);
    }

    // enum の default は member 値に依らず 0 (C# 意味論)
    [Fact]
    public void EnumField_DefaultIsZero_EvenWithoutZeroMember()
    {
        var result = TestHelper.TranspileAndRun("""
            public enum Level { Low = 1, High = 2 }
            public class Holder { public Level L; }
            public static class T
            {
                public static int Test() { return (int)new Holder().L; }
            }
            """, "T.Test()");

        Assert.Equal("0", result);
    }

    [Fact]
    public void StructEnumMember_ZeroInitialized()
    {
        var result = TestHelper.TranspileAndRun("""
            public enum Dir { Up, Down }
            public struct Cell { public Dir D; public int N; }
            public class Grid { public Cell C; }
            public static class T
            {
                public static bool Test()
                {
                    var g = new Grid();
                    Cell c = default;
                    return g.C.D == Dir.Up && c.D == Dir.Up;
                }
            }
            """, "tostring(T.Test())");

        Assert.Equal("true", result);
    }
}
