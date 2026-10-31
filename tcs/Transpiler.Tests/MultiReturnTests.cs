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
            [IoStub]);

        Assert.True(result.Success, string.Join("\n", result.Errors));

        var script = $$"""
            io = { load_text = function(path)
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
            [IoStub]);

        Assert.True(result.Success, string.Join("\n", result.Errors));

        var script = $$"""
            host = { try_read = function() return true, 42 end }
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
            [IoStub]);

        Assert.True(result.Success, string.Join("\n", result.Errors));

        var script = $$"""
            io = { load_text = function() return "t", 7, "s" end }
            {{result.Lua}}
            print(Game.Run())
            """;
        var output = TestHelper.RunLua(script).Trim();

        Assert.Equal("7", output);
    }

    [Fact]
    public void UserMethodOutParameter_ReturnsThroughLuaMultiReturn()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public static class Parser
            {
                public static bool TryParse(string text, out int value)
                {
                    value = 42;
                    return true;
                }

                public static int Run()
                {
                    Parser.TryParse("42", out var value);
                    return value;
                }
            }
            """]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Contains("return true, value", result.Lua);
        Assert.Equal("42", TestHelper.RunLua($"{result.Lua}\nprint(Parser.Run())").Trim());
    }

    // 条件の位置は closure を作らず文に下ろす
    [Fact]
    public void UserMethodOutCall_InCondition_DoesNotAllocateClosure()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public static class P
            {
                static bool Try(out int v) { v = 1; return true; }
                public static int Run() { if (Try(out var v)) return v; return 0; }
            }
            """]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        var run = result.Lua[result.Lua.IndexOf("function P.Run", StringComparison.Ordinal)..];
        Assert.DoesNotContain("(function()", run);
        Assert.Equal("1", TestHelper.RunLua($"{result.Lua}\nprint(P.Run())").Trim());
    }

    [Theory]
    [InlineData("int v; Try(out v);")]
    [InlineData("Try(out _f);")]
    [InlineData("var a = new int[1]; Try(out a[0]);")]
    [InlineData("new P().M(out _f);")]
    public void UserMethodOutArgument_NotADeclaration_ReportsUnsupportedSyntax(string statement)
    {
        var result = Transpiler.TranspileWithDiagnostics([$$"""
            public class P
            {
                static int _f;
                static bool Try(out int v) { v = 1; return true; }
                void M(out int v) { v = 1; }
                public static void Run() { {{statement}} }
            }
            """]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e =>
            e.Contains($"error {TinyCsDiagnosticIds.UnsupportedSyntax}:")
            && e.Contains("OutArgumentNotDeclaration"));
    }

    [Fact]
    public void UserMethodOutArgument_ForwardingOutParameter_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class P
            {
                static void A(out int v) { v = 1; }
                static void B(out int v) { A(out v); }
            }
            """]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("OutArgumentNotDeclaration"));
    }

    [Theory]
    [InlineData("public delegate void D(out int v);")]
    [InlineData("public class C { public C(out int v) { v = 1; } }")]
    public void OutParameter_OutsideMethodDeclaration_ReportsUnsupportedSyntax(string declaration)
    {
        var result = Transpiler.TranspileWithDiagnostics([declaration]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e =>
            e.Contains($"error {TinyCsDiagnosticIds.UnsupportedSyntax}:")
            && e.Contains("OutParameter"));
    }

    // host の --ref メソッドと BCL の TryGetValue は宣言済みの変数を受ける
    [Fact]
    public void OutArgument_ExistingLocal_StaysAllowedForNonUserMethods()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System.Collections.Generic;
            public static class Game
            {
                public static string Run()
                {
                    int value;
                    Host.try_read(out value);
                    var d = new Dictionary<string, int>();
                    d.TryGetValue("k", out value);
                    return "" + value;
                }
            }
            """], null, [IoStub]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
    }

    [Fact]
    public void UserMethodRefParameter_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public static class Counter
            {
                public static void Bump(ref int value)
                {
                    value = value + 1;
                }
            }
            """]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e =>
            e.Contains($"error {TinyCsDiagnosticIds.UnsupportedSyntax}:")
            && e.Contains("RefParameter"));
    }

    [Fact]
    public void RefStubOutParameter_DoesNotWarn()
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
            [IoStub]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain(result.Warnings, w =>
            w.Contains("OutParameter") || w.Contains("RefParameter"));
    }
}
