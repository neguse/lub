namespace TinyCs.Tests;

public class MultiReturnTests
{
    private const string IoStub = """
        public static class Io
        {
            public static void load_text(string path, out string text,
                out int version, out string status)
            {
                text = "";
                version = 0;
                status = "";
            }
        }

        public static class Host
        {
            public static bool try_read(out int value)
            {
                value = 0;
                return false;
            }
        }
        """;

    [Fact]
    public void RefMethodOutArgs_AssignsLuaMultiReturn()
    {
        var source = """
            public static class Game
            {
                public static string Run()
                {
                    Io.load_text("data.txt", out var text, out var version,
                        out var status);
                    return text + "," + version + "," + status;
                }
            }
            """;

        var result = Transpiler.TranspileWithDiagnostics([source], null,
            [IoStub], checkNaming: false);

        Assert.True(result.Success, string.Join("\n", result.Errors));

        var script = $$"""
            Io = { load_text = function(path)
              return "hello:" .. path, 3, "ready"
            end }
            {{result.Lua}}
            print(Game.Run())
            """;
        var output = TestHelper.RunLua(script).Trim();

        Assert.Equal("hello:data.txt,3,ready", output);
    }

    [Fact]
    public void RefMethodOutArgs_NonVoidReturn_UsableInCondition()
    {
        var source = """
            public static class Game
            {
                public static string Run()
                {
                    if (Host.try_read(out var value))
                    {
                        return "ok:" + value;
                    }
                    return "none";
                }
            }
            """;

        var result = Transpiler.TranspileWithDiagnostics([source], null,
            [IoStub], checkNaming: false);

        Assert.True(result.Success, string.Join("\n", result.Errors));

        var script = $$"""
            Host = { try_read = function() return true, 42 end }
            {{result.Lua}}
            print(Game.Run())
            """;
        var output = TestHelper.RunLua(script).Trim();

        Assert.Equal("ok:42", output);
    }

    [Fact]
    public void RefMethodOutArgs_DiscardMapsToUnderscore()
    {
        var source = """
            public static class Game
            {
                public static int Run()
                {
                    Io.load_text("x", out _, out var version, out _);
                    return version;
                }
            }
            """;

        var result = Transpiler.TranspileWithDiagnostics([source], null,
            [IoStub], checkNaming: false);

        Assert.True(result.Success, string.Join("\n", result.Errors));

        var script = $$"""
            Io = { load_text = function() return "t", 7, "s" end }
            {{result.Lua}}
            print(Game.Run())
            """;
        var output = TestHelper.RunLua(script).Trim();

        Assert.Equal("7", output);
    }
}
