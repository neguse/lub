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
            $"{result.Lua}\nprint(Locker.test())").Trim());
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
            print(tostring(NameDemo.simple(1)) .. "|" ..
                tostring(NameDemo.member_name()) .. "|" ..
                tostring(NameDemo.type_name()))
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
            $"{transpile.Lua}\nprint(NameDemo.run())").Trim());
    }

    // #19: 名前付き引数は check でも build でも error。build は Lua を書かず
    // exit 1 (watch / lub の経路が誤った Lua を掴まない)
    [Fact]
    public void Check_NamedArgumentReportsError()
    {
        var result = RunCli(NamedArgumentSource, check: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(3, CountDiagnostics(result.Stderr,
            TinyCsDiagnosticIds.UnsupportedSyntax, severity: "error"));
        Assert.Contains(
            "input.cs(8,39): error TCS1001: unsupported syntax: NamedArgument",
            result.Stderr);
        Assert.Contains(
            "input.cs(9,36): error TCS1001: unsupported syntax: NamedArgument",
            result.Stderr);
        Assert.Contains(
            "input.cs(9,42): error TCS1001: unsupported syntax: NamedArgument",
            result.Stderr);
    }

    [Fact]
    public void Transpile_NamedArgumentFailsWithoutOutput()
    {
        var result = RunCli(NamedArgumentSource, check: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal("", result.Lua);
        Assert.Equal(3, CountDiagnostics(result.Stderr,
            TinyCsDiagnosticIds.UnsupportedSyntax, severity: "error"));
        Assert.Contains("NamedArgument", result.Stderr);
        Assert.DoesNotContain("Wrote ", result.Stderr);
    }

    [Fact]
    public void Check_LuaNameCollisionIsErrorExit1()
    {
        var result = RunCli(NameCollisionSource, check: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error TCS1001: unsupported syntax: LuaNameCollision(flash/Flash): 'flash' and 'Flash' both map to Lua 'flash'",
            result.Stderr);
    }

    [Fact]
    public void Transpile_LuaNameCollisionIsErrorWithoutOutput()
    {
        var result = RunCli(NameCollisionSource, check: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error TCS1001: unsupported syntax: LuaNameCollision(flash/Flash): 'flash' and 'Flash' both map to Lua 'flash'",
            result.Stderr);
        Assert.Equal("", result.Lua);
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
            var args = check
                ? new List<string> { "check", inputPath }
                : [inputPath, "-o", outputPath, "--no-runtime"];
            if (noNamingCheck) args.Add("--no-naming-check");
            var (exitCode, stdoutText, stderrText) =
                ConsoleCapture.Run(() => Program.Main([.. args]));
            var lua = File.Exists(outputPath)
                ? File.ReadAllText(outputPath)
                : "";
            return (exitCode, stdoutText, stderrText, lua);
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); }
            catch (IOException) { }
        }
    }

    private static int CountDiagnostics(string text, string diagnosticId,
        string severity = "warning") =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains($"{severity} {diagnosticId}:"));

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

    // neguse/tcs#19 の再現
    private const string NamedArgumentSource = """
        public static class B1User
        {
            static string F(int a, int? version = null, int? count = null)
                => "a=" + a + " version=" + (version ?? -1) + " count=" + (count ?? -1);
            static string G(int x, int y) => "x=" + x + " y=" + y;
            public static void Main()
            {
                System.Console.WriteLine(F(1, count: 5));
                System.Console.WriteLine(G(y: 1, x: 2));
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

    private const string NameCollisionSource = """
        public class Repro
        {
            public int flash;
            public void Flash() { flash = 1; }
        }
        """;
}
