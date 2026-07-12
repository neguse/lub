namespace TinyCs.Tests;

public class InterfaceTests
{
    [Fact]
    public void Interface_ClassImplementation()
    {
        var result = TestHelper.TranspileAndRun("""
            public interface IGreeter
            {
                string Greet(string name);
            }
            public class Hello : IGreeter
            {
                public string Greet(string name)
                {
                    return "Hello, " + name;
                }
            }
            public class T
            {
                public static string Test()
                {
                    var h = new Hello();
                    return h.Greet("world");
                }
            }
            """,
            "T.Test()");
        Assert.Equal("Hello, world", result);
    }

    [Fact]
    public void Interface_MultipleInterfaces()
    {
        var result = TestHelper.TranspileAndRun("""
            public interface IMovable
            {
                int Speed { get; }
            }
            public interface IDamageable
            {
                int Health { get; }
            }
            public class Player : IMovable, IDamageable
            {
                public int Speed = 10;
                public int Health = 100;
            }
            public class T
            {
                public static int Test()
                {
                    var p = new Player();
                    return p.Speed + p.Health;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("110", result);
    }

    [Fact]
    public void Interface_GetSetProperty_CanUseMutableField()
    {
        var result = TestHelper.TranspileAndRun("""
            public interface ICounter
            {
                int Value { get; set; }
            }
            public class Counter : ICounter
            {
                public int Value = 1;
            }
            public class T
            {
                public static int Test()
                {
                    var counter = new Counter();
                    counter.Value = 5;
                    return counter.Value;
                }
            }
            """, "T.Test()");

        Assert.Equal("5", result);
    }

    [Fact]
    public void Interface_GetOnlyProperty_CanUseReadonlyField()
    {
        var result = TestHelper.TranspileAndRun("""
            public interface IValue
            {
                int Value { get; }
            }
            public class T : IValue
            {
                public readonly int Value = 7;
            }
            """, "T.new().Value");

        Assert.Equal("7", result);
    }
}
