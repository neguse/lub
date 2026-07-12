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

            var oldOut = Console.Out;
            var oldErr = Console.Error;
            int exitCode;
            using (var stdout = new StringWriter())
            using (var stderr = new StringWriter())
            {
                try
                {
                    Console.SetOut(stdout);
                    Console.SetError(stderr);
                    exitCode = Program.Main([inputPath, "-o", outputPath,
                        "--entry", "App", "--no-runtime"]);
                }
                finally
                {
                    Console.SetOut(oldOut);
                    Console.SetError(oldErr);
                }
            }

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
}
