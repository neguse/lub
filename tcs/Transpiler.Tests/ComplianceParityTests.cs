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
    public void Transpile_PartialTypeAndLockFailWithoutOutput()
    {
        var result = RunCli(PartialLockSource, check: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal("", result.Lua);
        Assert.Equal(5, CountDiagnostics(result.Stderr,
            TinyCsDiagnosticIds.UnsupportedSyntax));
    }

    [Fact]
    public void Check_NameOfIsSupported()
    {
        var result = RunCli(NameOfSource, check: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(0, CountDiagnostics(result.Stderr,
            TinyCsDiagnosticIds.UnsupportedSyntax));
        Assert.Equal(0, CountDiagnostics(result.Stderr,
            TinyCsDiagnosticIds.UnsupportedApi));
    }
    [Fact]
    public void Transpile_NameOfFoldsToConstantStrings()
    {
        var result = RunCli(NameOfSource, check: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(0, CountDiagnostics(result.Stderr,
            TinyCsDiagnosticIds.UnsupportedSyntax));
        Assert.DoesNotContain("nameof(", result.Lua);
        Assert.DoesNotContain("unsupported: NameOfExpression", result.Lua);
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
        var check = RunCli(UserNameofSource, check: true);
        var transpile = RunCli(UserNameofSource, check: false);

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

    // #19: 名前付き引数は check でも build でも error。build は Lua を書かず
    // exit 1 (watch / lub の経路が誤った Lua を掴まない)
    [Fact]
    public void Check_NamedArgumentReportsError()
    {
        var result = RunCli(NamedArgumentSource, check: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(3, CountDiagnostics(result.Stderr,
            TinyCsDiagnosticIds.UnsupportedSyntax));
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
            TinyCsDiagnosticIds.UnsupportedSyntax));
        Assert.Contains("NamedArgument", result.Stderr);
        Assert.DoesNotContain("Wrote ", result.Stderr);
    }

    [Fact]
    public void Check_LuaNameCollisionIsErrorExit1()
    {
        var result = RunCli(NameCollisionSource, check: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error TCS1001: unsupported syntax: LuaNameCollision(end_/end): 'end_' and 'end' both map to Lua 'end_'",
            result.Stderr);
    }

    [Fact]
    public void Transpile_LuaNameCollisionIsErrorWithoutOutput()
    {
        var result = RunCli(NameCollisionSource, check: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error TCS1001: unsupported syntax: LuaNameCollision(end_/end): 'end_' and 'end' both map to Lua 'end_'",
            result.Stderr);
        Assert.Equal("", result.Lua);
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
            var args = check
                ? new List<string> { "check", inputPath }
                : [inputPath, "-o", outputPath, "--no-runtime"];
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
        string severity = "error") =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains($"{severity} {diagnosticId}:"));

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
            public int end_;
            public void end() { end_ = 1; }
        }
        """;
}
