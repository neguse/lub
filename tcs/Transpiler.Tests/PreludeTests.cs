namespace TinyCs.Tests;

[Collection(ConsoleCollection.Name)]
public class PreludeTests
{
    [Fact]
    public void Cli_PreludeOption_PrependsUserLuaBeforeGeneratedCode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            $"tcs_prelude_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var inputPath = Path.Combine(tempDir, "app.cs");
            File.WriteAllText(inputPath, """
                public static class App
                {
                    public static int Read()
                    {
                        return Host.value();
                    }
                }
                """);
            var refPath = Path.Combine(tempDir, "stub.cs");
            File.WriteAllText(refPath, """
                public static class Host
                {
                    public static int value()
                    {
                        return 0;
                    }
                }
                """);
            var preludePath = Path.Combine(tempDir, "shim.lua");
            File.WriteAllText(preludePath, """
                Host = { value = function() return 42 end }
                """);
            var outputPath = Path.Combine(tempDir, "app.lua");

            var result = RunCli(inputPath, "--ref", refPath,
                "--prelude", preludePath, "-o", outputPath,
                "--entry", "App", "--no-naming-check");

            Assert.Equal(0, result.ExitCode);
            var script = $"""
                local m = dofile("{outputPath}")
                print(m.Read())
                """;
            var output = TestHelper.RunLua(script).Trim();

            Assert.Equal("42", output);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Cli_PreludeOption_MissingFile_ReturnsError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            $"tcs_prelude_missing_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var inputPath = Path.Combine(tempDir, "app.cs");
            File.WriteAllText(inputPath, "public static class App { }");

            var result = RunCli(inputPath, "--prelude",
                Path.Combine(tempDir, "missing.lua"));

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("prelude file not found", result.Stderr);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(
        params string[] args)
    {
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exitCode = Program.Main(args);
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }
}
