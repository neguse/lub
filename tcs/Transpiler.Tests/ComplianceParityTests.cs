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

    [Fact]
    public void Check_NameOfReportsSharedSyntaxDiagnostics()
    {
        var result = RunCli(NameOfSource, check: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        AssertNameOfDiagnostics(result.Stderr);
        Assert.Equal(0, CountDiagnostics(result.Stderr,
            TinyCsDiagnosticIds.UnsupportedApi));
    }

    [Fact]
    public void Transpile_NameOfKeepsValidConstantFallbacks()
    {
        var result = RunCli(NameOfSource, check: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stdout);
        AssertNameOfDiagnostics(result.Stderr);
        Assert.Equal(0, CountDiagnostics(result.Stderr,
            TinyCsDiagnosticIds.UnsupportedApi));
        Assert.Equal(3, result.Lua.Split(
            "--[[ unsupported: NameOfExpression ]]",
            StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("nameof(", result.Lua);
        Assert.Equal("value|E|DateTime", TestHelper.RunLua($$"""
            {{result.Lua}}
            print(tostring(NameDemo.Simple(1)) .. "|" ..
                tostring(NameDemo.MemberName()) .. "|" ..
                tostring(NameDemo.TypeName()))
            """).Trim());
    }

    [Fact]
    public void UserMethodNamedNameof_RemainsOrdinaryInvocation()
    {
        var check = RunCli(UserNameofSource, check: true,
            noNamingCheck: true);
        var transpile = RunCli(UserNameofSource, check: false,
            noNamingCheck: true);

        Assert.Equal(0, check.ExitCode);
        Assert.Empty(check.Stdout);
        Assert.Empty(check.Stderr);
        Assert.Equal(0, transpile.ExitCode);
        Assert.Empty(transpile.Stdout);
        Assert.Equal(0, CountDiagnostics(transpile.Stderr,
            TinyCsDiagnosticIds.UnsupportedSyntax));
        Assert.Equal("ok", TestHelper.RunLua(
            $"{transpile.Lua}\nprint(NameDemo.Run())").Trim());
    }

    private static (int ExitCode, string Stdout, string Stderr, string Lua)
        RunCli(string source, bool check, bool noNamingCheck = false)
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
                    ? new List<string> { "check", inputPath }
                    : [inputPath, "-o", outputPath, "--no-runtime"];
                if (noNamingCheck) args.Add("--no-naming-check");
                var exitCode = Program.Main([.. args]);
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

    private static void AssertNameOfDiagnostics(string stderr)
    {
        var diagnostics = stderr.Split('\n',
                StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(
                $"warning {TinyCsDiagnosticIds.UnsupportedSyntax}:"))
            .ToArray();

        Assert.Equal(3, diagnostics.Length);
        Assert.Collection(diagnostics,
            line => Assert.Contains(
                "input.cs(3,47): warning TCS1001: unsupported syntax: NameOfExpression",
                line),
            line => Assert.Contains(
                "input.cs(4,42): warning TCS1001: unsupported syntax: NameOfExpression",
                line),
            line => Assert.Contains(
                "input.cs(5,40): warning TCS1001: unsupported syntax: NameOfExpression",
                line));
    }

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

    private const string NameOfSource = """
        public class NameDemo
        {
            public static string Simple(int value) => nameof(value);
            public static string MemberName() => nameof(System.Math.E);
            public static string TypeName() => nameof(System.DateTime);
        }
        """;

    private const string UserNameofSource = """
        public class NameDemo
        {
            public static string nameof(string value) => value;
            public static string Run() => nameof("ok");
        }
        """;
}
