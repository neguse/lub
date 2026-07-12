namespace TinyCs.Tests;

[Collection(ConsoleCollection.Name)]
public class ComplianceParityTests
{
    [Fact]
    public void Check_PartialTypeAndLockReportSharedSyntaxDiagnostics()
    {
        var result = RunCli(PartialLockSource, check: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(5, CountDiagnostics(result.Stderr,
            TinyCsDiagnosticIds.UnsupportedSyntax));
        Assert.Contains("PartialTypeDeclaration", result.Stderr);
        Assert.Contains("LockStatement", result.Stderr);
    }

    [Fact]
    public void Transpile_PartialTypeAndLockKeepDiagnosticFallbacks()
    {
        var result = RunCli(PartialLockSource, check: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(5, CountDiagnostics(result.Stderr,
            TinyCsDiagnosticIds.UnsupportedSyntax));
        Assert.Equal(4, result.Lua.Split(
            "--[[ unsupported: PartialTypeDeclaration ]]",
            StringSplitOptions.None).Length - 1);
        Assert.Contains("--[[ unsupported: LockStatement ]]", result.Lua);
        Assert.DoesNotContain("PartialClass = {}", result.Lua);
        Assert.DoesNotContain("PartialRecord = {}", result.Lua);
        Assert.Equal("1", TestHelper.RunLua(
            $"{result.Lua}\nprint(Locker.Test())").Trim());
    }

    private static (int ExitCode, string Stdout, string Stderr, string Lua)
        RunCli(string source, bool check)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(),
            $"tcs_compliance_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var inputPath = Path.Combine(tempDirectory, "input.cs");
            var outputPath = Path.Combine(tempDirectory, "output.lua");
            File.WriteAllText(inputPath, source);
            var oldOut = Console.Out;
            var oldErr = Console.Error;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var args = check
                    ? new[] { "check", inputPath }
                    : new[] { inputPath, "-o", outputPath, "--no-runtime" };
                var exitCode = Program.Main(args);
                var lua = File.Exists(outputPath)
                    ? File.ReadAllText(outputPath)
                    : "";
                return (exitCode, stdout.ToString(), stderr.ToString(), lua);
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); }
            catch (IOException) { }
        }
    }

    private static int CountDiagnostics(string text, string diagnosticId) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains($"warning {diagnosticId}:"));

    private const string PartialLockSource = """
        public partial class PartialClass
        {
            public static int First() => 1;
        }
        public partial class PartialClass
        {
            public static int Second() => 2;
        }

        public partial record PartialRecord;
        public partial interface IPartial { }

        public class Locker
        {
            public static int Test()
            {
                lock (new object())
                {
                    return 1;
                }
            }
        }
        """;
}
