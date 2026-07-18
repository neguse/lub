namespace TinyCs.Tests;

public class ObjectInitializerTests
{
    [Fact]
    public void ObjectInitializer_OnEmittedClass_AssignsFields()
    {
        var source = """
            public class Point
            {
                public int X;
                public int Y;
            }

            public static class Test
            {
                public static int Run()
                {
                    var p = new Point { X = 3, Y = 4 };
                    return p.X + p.Y;
                }
            }
            """;

        var result = TestHelper.TranspileAndRun(source, "Test.Run()");

        Assert.Equal("7", result);
    }

    [Fact]
    public void ObjectInitializer_OnEmittedClass_RunsConstructorFirst()
    {
        var source = """
            public class Counter
            {
                public int Value;

                public Counter(int start)
                {
                    Value = start;
                }
            }

            public static class Test
            {
                public static int Run()
                {
                    var c = new Counter(10) { Value = 42 };
                    return c.Value;
                }
            }
            """;

        var result = TestHelper.TranspileAndRun(source, "Test.Run()");

        Assert.Equal("42", result);
    }

    [Fact]
    public void ObjectInitializer_OnRefType_EmitsTableLiteral()
    {
        var refSource = """
            public class Opts
            {
                public int x;
                public float[]? color;
            }

            public static class Host
            {
                public static void send(Opts opts) { }
            }
            """;
        var source = """
            public static class Game
            {
                public static void Run()
                {
                    Host.send(new Opts { x = 5, color = new float[] { 0.5f, 1.0f } });
                }
            }
            """;

        var result = Transpiler.TranspileWithDiagnostics(
            [source], null, [refSource]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain("Opts", result.Lua);

        var script = $$"""
            local captured = nil
            Host = { send = function(opts) captured = opts end }
            {{result.Lua}}
            Game.Run()
            print(captured.x .. "," .. captured.color[2] .. "," .. #captured.color)
            """;
        var output = TestHelper.RunLua(script).Trim();

        Assert.Equal("5,1.0,2", output);
    }

    [Fact]
    public void ObjectInitializer_OnRefType_WithoutInitializer_EmitsEmptyTable()
    {
        var refSource = """
            public class Opts
            {
                public int x;
            }

            public static class Host
            {
                public static void send(Opts opts) { }
            }
            """;
        var source = """
            public static class Game
            {
                public static void Run()
                {
                    Host.send(new Opts());
                }
            }
            """;

        var result = Transpiler.TranspileWithDiagnostics(
            [source], null, [refSource]);

        Assert.True(result.Success, string.Join("\n", result.Errors));

        var script = $$"""
            local captured = nil
            Host = { send = function(opts) captured = opts end }
            {{result.Lua}}
            Game.Run()
            print(type(captured) .. "," .. tostring(next(captured)))
            """;
        var output = TestHelper.RunLua(script).Trim();

        Assert.Equal("table,nil", output);
    }

    [Fact]
    public void ObjectInitializer_NestedInitializer_ReportsWarning()
    {
        var source = """
            public class Inner
            {
                public int A;
            }

            public class Outer
            {
                public Inner? Child;
            }

            public static class Test
            {
                public static void Run()
                {
                    var o = new Outer { Child = { A = 1 } };
                }
            }
            """;

        var result = Transpiler.TranspileWithDiagnostics([source]);

        Assert.Contains(result.Warnings,
            w => w.Contains("TCS1001") && w.Contains("initializer"));
    }
}
