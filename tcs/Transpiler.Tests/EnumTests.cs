namespace TinyCs.Tests;

// The first four tests write implicit conversions between enum and int. C# gives a compile error and tcs accepts them, so they are not compared with .NET (dotnet: false)
public class EnumTests
{
    [Fact]
    public void BasicEnum()
    {
        var result = TestHelper.CompileAndRunVm("""
            public enum Direction { Up, Down, Left, Right }

            public class Nav
            {
                public static int GetDir() { return Direction.Right; }
            }
            """,
            "Nav.GetDir()", dotnet: false);
        Assert.Equal("3", result);
    }

    [Fact]
    public void EnumWithExplicitValues()
    {
        var result = TestHelper.CompileAndRunVm("""
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
            "Game.Speed(10)", dotnet: false);
        Assert.Equal("10", result);
    }

    [Fact]
    public void EnumComparison()
    {
        var result = TestHelper.CompileAndRunVmMain("""
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
            """, "var m = new Machine(); m.Activate(); System.Console.WriteLine(m.IsActive());", dotnet: false);
        Assert.Equal("True", result);
    }

    [Fact]
    public void IntegerToEnum_AndNotEquals_AreAllowed()
    {
        var result = TestHelper.CompileAndRunVm("""
            public enum State { Idle, Active, Done }

            public class T
            {
                public static bool Test()
                {
                    State state = 1;
                    return state != 0;
                }
            }
            """, "T.Test()", dotnet: false);

        Assert.Equal("True", result);
    }

    // #14: the default value of an enum-typed field / property without an initializer is 0 (default(E))
    [Fact]
    public void EnumField_DefaultIsZero()
    {
        var result = TestHelper.CompileAndRunVm("""
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
            """, "T.Test()");

        Assert.Equal("True", result);
    }

    // The default of an enum is 0 regardless of the member values (C# semantics)
    [Fact]
    public void EnumField_DefaultIsZero_EvenWithoutZeroMember()
    {
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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
            """, "T.Test()");

        Assert.Equal("True", result);
    }
}
