namespace TinyCs.Tests;

[Collection(ConsoleCollection.Name)]
public class NamingSuppressionTests
{
    private const string LowerCamelSource = """
        public static class App
        {
            public static void onInit()
            {
            }
        }
        """;

    [Fact]
    public void NamingCheck_DefaultOn_WarnsLowerCamelMethod()
    {
        var result = Transpiler.TranspileWithDiagnostics([LowerCamelSource]);

        Assert.Contains(result.Warnings, w => w.Contains("naming:"));
    }

    [Fact]
    public void NamingCheck_Disabled_SuppressesNamingWarnings()
    {
        var result = Transpiler.TranspileWithDiagnostics([LowerCamelSource],
            checkNaming: false);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("naming:"));
    }

    [Fact]
    public void NamingCheck_Disabled_KeepsComplianceDiagnostics()
    {
        var source = """
            public static class App
            {
                public static void onInit()
                {
                    try { } finally { }
                }
            }
            """;

        var result = Transpiler.TranspileWithDiagnostics([source],
            checkNaming: false);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("naming:"));
        Assert.Contains(result.Warnings, w => w.Contains("TCS1001"));
    }

    [Fact]
    public void Cli_Check_NoNamingCheck_ExitsZeroForLowerCamel()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            $"tcs_naming_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var inputPath = Path.Combine(tempDir, "app.cs");
            File.WriteAllText(inputPath, LowerCamelSource);

            var withCheck = RunCli("check", inputPath);
            var withoutCheck = RunCli("check", inputPath, "--no-naming-check");

            Assert.Equal(1, withCheck.ExitCode);
            Assert.Equal(0, withoutCheck.ExitCode);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(
        params string[] args) =>
        ConsoleCapture.Run(() => Program.Main(args));
}
