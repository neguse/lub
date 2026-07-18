namespace TinyCs.Tests;

[Collection(ConsoleCollection.Name)]
public class EntryClassTests
{
    private const string AppSource = """
        public static class App
        {
            public static string Ping()
            {
                return "pong";
            }
        }
        """;

    [Fact]
    public void EntryClass_AppendsModuleReturn_RequireReturnsClassTable()
    {
        var result = Transpiler.TranspileWithDiagnostics([AppSource],
            entryClass: "App");

        Assert.True(result.Success, string.Join("\n", result.Errors));

        var luaPath = Path.Combine(Path.GetTempPath(),
            $"tcs_entry_{Guid.NewGuid():N}.lua");
        try
        {
            File.WriteAllText(luaPath, result.Lua);
            var script = $"""
                local m = dofile("{luaPath}")
                print(m.Ping())
                """;
            var output = TestHelper.RunLua(script).Trim();

            Assert.Equal("pong", output);
        }
        finally
        {
            File.Delete(luaPath);
        }
    }

    [Fact]
    public void EntryClass_NotFound_ReportsError()
    {
        var result = Transpiler.TranspileWithDiagnostics([AppSource],
            entryClass: "Missing");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("entry class not found"));
    }

    [Fact]
    public void EntryClass_ReferenceOnlyType_ReportsError()
    {
        var refSource = """
            public static class Host
            {
                public static void ping() { }
            }
            """;

        var result = Transpiler.TranspileWithDiagnostics([AppSource], null,
            [refSource], entryClass: "Host");

        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            e => e.Contains("reference-only") && e.Contains("Host"));
    }

    [Fact]
    public void Cli_EntryOption_EmitsModuleReturn()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            $"tcs_entry_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var inputPath = Path.Combine(tempDir, "app.cs");
            File.WriteAllText(inputPath, AppSource);
            var outputPath = Path.Combine(tempDir, "app.lua");

            var (exitCode, _, _) = ConsoleCapture.Run(
                () => Program.Main([inputPath, "-o", outputPath,
                    "--entry", "App", "--no-runtime"]));

            Assert.Equal(0, exitCode);
            var script = $"""
                local m = dofile("{outputPath}")
                print(m.Ping())
                """;
            var output = TestHelper.RunLua(script).Trim();

            Assert.Equal("pong", output);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // T155: --entry は emitter の実 Lua 名 (namespace 透過の simple 名) を
    // return する。namespaced 指定・一意な simple 指定の両方が動き、
    // interface / 曖昧な simple 名は exit 1。
    private const string NamespacedSource = """
        namespace Game
        {
            public static class App
            {
                public static string Ping()
                {
                    return "pong";
                }
            }
        }
        """;

    private static string RunEntryModule(string lua, string call)
    {
        var luaPath = Path.Combine(Path.GetTempPath(),
            $"tcs_entry_{Guid.NewGuid():N}.lua");
        try
        {
            File.WriteAllText(luaPath, lua);
            return TestHelper.RunLua($"""
                local m = dofile("{luaPath}")
                print({call})
                """).Trim();
        }
        finally
        {
            File.Delete(luaPath);
        }
    }

    [Fact]
    public void EntryClass_NamespaceQualified_ReturnsEmittedTable()
    {
        var result = Transpiler.TranspileWithDiagnostics([NamespacedSource],
            entryClass: "Game.App");

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Equal("pong", RunEntryModule(result.Lua, "m.Ping()"));
    }

    [Fact]
    public void EntryClass_UniqueSimpleNameInNamespace_Resolves()
    {
        var result = Transpiler.TranspileWithDiagnostics([NamespacedSource],
            entryClass: "App");

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Equal("pong", RunEntryModule(result.Lua, "m.Ping()"));
    }

    [Fact]
    public void EntryClass_Interface_ReportsError()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public interface IApp
            {
                void Run();
            }
            public class App : IApp
            {
                public void Run() { }
            }
            """], entryClass: "IApp");

        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            e => e.Contains("entry class must be a class"));
    }

    [Fact]
    public void EntryClass_AmbiguousSimpleName_ReportsError()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            namespace Alpha
            {
                public static class App
                {
                    public static int N() => 1;
                }
            }
            namespace Beta
            {
                public static class App
                {
                    public static int N() => 2;
                }
            }
            """], entryClass: "App");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("ambiguous"));
    }
}
