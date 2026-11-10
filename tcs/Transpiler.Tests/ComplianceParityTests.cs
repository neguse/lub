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
    public void BuildC_PartialTypeAndLockFailWithoutOutput()
    {
        var result = RunCli(PartialLockSource, check: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Null(result.Output);
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
    public void NameOf_FoldsToConstantStrings()
    {
        Assert.Equal("value|E|DateTime", TestHelper.CompileAndRunVm(NameOfSource,
            "NameDemo.Simple(1) + \"|\" + NameDemo.MemberName() + \"|\" + NameDemo.TypeName()"));
    }

    [Fact]
    public void UserMethodNamedNameof_RemainsOrdinaryInvocation()
    {
        var check = RunCli(UserNameofSource, check: true);

        Assert.Equal(0, check.ExitCode);
        Assert.Empty(check.Stdout);
        Assert.Empty(check.Stderr);
        Assert.Equal("ok", TestHelper.CompileAndRunVm(UserNameofSource, "NameDemo.Run()"));
    }

    // #19: 名前付き引数は check でも build でも error。build (tcs c) は出力を書かずに exit 1
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
    public void BuildC_NamedArgumentFailsWithoutOutput()
    {
        var result = RunCli(NamedArgumentSource, check: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Null(result.Output);
        Assert.Equal(3, CountDiagnostics(result.Stderr,
            TinyCsDiagnosticIds.UnsupportedSyntax));
        Assert.Contains("NamedArgument", result.Stderr);
    }

    [Fact]
    public void Check_ReservedMemberIsErrorExit1()
    {
        var result = RunCli(ReservedMemberSource, check: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error TCS1001: unsupported syntax: ReservedIdentifier(self)",
            result.Stderr);
        Assert.DoesNotContain("self_", result.Stderr);
    }

    [Fact]
    public void BuildC_ReservedMemberIsErrorWithoutOutput()
    {
        var result = RunCli(ReservedMemberSource, check: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error TCS1001: unsupported syntax: ReservedIdentifier(self)",
            result.Stderr);
        Assert.Null(result.Output);
    }

    // check なら tcs check、そうでなければ tcs c で output.c に書く (書かなければ Output は null)
    private static (int ExitCode, string Stdout, string Stderr, string? Output)
        RunCli(string source, bool check)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(),
            $"tcs_compliance_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var inputPath = Path.Combine(tempDirectory, "input.cs");
            var outputPath = Path.Combine(tempDirectory, "output.c");
            File.WriteAllText(inputPath, source);
            var args = check
                ? new List<string> { "check", inputPath }
                : ["c", inputPath, "-o", outputPath];
            var (exitCode, stdoutText, stderrText) =
                ConsoleCapture.Run(() => Program.Main([.. args]));
            var output = File.Exists(outputPath)
                ? File.ReadAllText(outputPath)
                : null;
            return (exitCode, stdoutText, stderrText, output);
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

    private const string ReservedMemberSource = """
        public class Repro
        {
            public int self_;
            public void self() { self_ = 1; }
        }
        """;
}
