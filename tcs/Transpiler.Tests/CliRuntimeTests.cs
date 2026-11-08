namespace TinyCs.Tests;

[Collection(ConsoleCollection.Name)]
public class CliRuntimeTests
{
    [Fact]
    public void Cli_Help_PrintsUsage()
    {
        var result = RunCli("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage: tcs check", result.Stdout);
        Assert.Contains("tcs vm", result.Stdout);
        Assert.Contains("tcs c", result.Stdout);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public void Cli_Version_PrintsVersion()
    {
        var result = RunCli("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("tcs 0.1.0", result.Stdout);
        Assert.Empty(result.Stderr);
    }

    // subcommand の無い呼び出しは usage を stderr に書いて exit 1 (入力のファイルを渡しても変換しない)
    [Theory]
    [InlineData]
    [InlineData("input.cs")]
    [InlineData("input.cs", "-o", "out.lua")]
    [InlineData("--unknown")]
    public void Cli_WithoutSubcommand_PrintsUsageAndFails(params string[] args)
    {
        var result = RunCli(args);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("Usage: tcs check", result.Stderr);
    }

    [Fact]
    public void Cli_UnknownOption_ReturnsError()
    {
        Assert.Contains("unknown option for check: --unknown", RunCli("check", "--unknown").Stderr);
        var c = RunCli("c", "--unknown");
        Assert.Equal(1, c.ExitCode);
        Assert.Contains("tcs2c: unknown option: --unknown", c.Stderr);
    }

    [Fact]
    public void Cli_OutputOptionMissingValue_ReturnsError()
    {
        var c = RunCli("c", "input.cs", "-o");
        Assert.Equal(1, c.ExitCode);
        Assert.Contains("tcs2c: missing value for -o", c.Stderr);
        var vm = RunCli("vm", "input.cs", "-o");
        Assert.Equal(1, vm.ExitCode);
        Assert.Contains("tcs2vm: missing value for -o", vm.Stderr);
        var trace = RunCli("vm", "input.cs", "--trace");
        Assert.Equal(1, trace.ExitCode);
        Assert.Contains("tcs2vm: missing value for --trace", trace.Stderr);
    }

    [Fact]
    public void Cli_RefOptionMissingValue_ReturnsError()
    {
        var result = RunCli("check", "input.cs", "--ref");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("missing value for --ref", result.Stderr);
    }

    [Fact]
    public void Cli_MissingInputFile_ReturnsError()
    {
        using var temp = TempDir.Create();
        var missing = temp.PathFor("missing.cs");
        var input = temp.Write("input.cs", ValidSource);

        var check = RunCli("check", missing);
        Assert.Equal(1, check.ExitCode);
        Assert.Contains("file not found: " + missing, check.Stderr);
        var checkRef = RunCli("check", input, "--ref", missing);
        Assert.Equal(1, checkRef.ExitCode);
        Assert.Contains("ref file not found: " + missing, checkRef.Stderr);
        var c = RunCli("c", missing, "-o", temp.PathFor("out.c"));
        Assert.Equal(1, c.ExitCode);
        Assert.Contains("tcs2c: could not find file: " + missing, c.Stderr);
        var vm = RunCli("vm", missing, "-o", temp.PathFor("out.tcsb"));
        Assert.Equal(1, vm.ExitCode);
        Assert.Contains("tcs2vm: could not find file: " + missing, vm.Stderr);
        var vmRef = RunCli("vm", input, "--ref", missing, "-o", temp.PathFor("out.tcsb"));
        Assert.Equal(1, vmRef.ExitCode);
        Assert.Contains("tcs2vm: could not find file: " + missing, vmRef.Stderr);
        Assert.False(File.Exists(temp.PathFor("out.c")));
        Assert.False(File.Exists(temp.PathFor("out.tcsb")));
    }

    [Fact]
    public void Cli_Check_ValidCode_ReturnsSuccess()
    {
        using var temp = TempDir.Create();
        var inputPath = temp.Write("app.cs", """
            public class T
            {
                public static int Test() => 42;
            }
            """);

        var result = RunCli("check", inputPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Empty(result.Stderr);
    }

    // `as` の先が class の式は準拠の検査を通るが IL にできない。check は IL を組み立てて TCS1001 と理由で止め、
    // tcs c も同じ理由で止める (初期化子を既定値にした C を書かない)
    [Fact]
    public void Cli_CheckAndC_StopOnExpressionsTheIlBuilderCannotBuild()
    {
        using var temp = TempDir.Create();
        var inputPath = temp.Write("app.cs", """
            public class Foo { }
            public static class P
            {
                static object o = new Foo();
                static Foo? f = o as Foo;
                public static void Main() { System.Console.WriteLine(f == null); }
            }
            """);
        var outputPath = Path.Combine(Path.GetDirectoryName(inputPath)!, "app.c");

        var check = RunCli("check", inputPath);
        var c = RunCli("c", "--entry", "P", inputPath, "-o", outputPath);

        Assert.Equal(1, check.ExitCode);
        Assert.Contains($"{inputPath}(5,21): error TCS1001: unsupported syntax: As", check.Stderr);
        Assert.Contains($"internal: IlBuild: as (As at {inputPath}:5)", check.Stderr);
        Assert.Equal(1, c.ExitCode);
        Assert.Contains($"internal: IlBuild: as (As at {inputPath}:5)", c.Stderr);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void Cli_Check_FullyQualifiedApisUseMemberDiagnosticsOnly()
    {
        using var temp = TempDir.Create();
        var supportedPath = temp.Write("supported.cs", """
            public class Supported
            {
                public static int Test() => System.Math.Min(3, 7);
            }
            """);
        var unsupportedPath = temp.Write("unsupported.cs", """
            public class Unsupported
            {
                public static string Test() =>
                    System.IO.File.ReadAllText("save.dat");
            }
            """);

        var supported = RunCli("check", supportedPath);
        var unsupported = RunCli("check", unsupportedPath);

        Assert.Equal(0, supported.ExitCode);
        Assert.Empty(supported.Stdout);
        Assert.Empty(supported.Stderr);
        Assert.Equal(1, unsupported.ExitCode);
        Assert.Empty(unsupported.Stdout);
        Assert.Equal(1, CountDiagnostics(unsupported.Stderr,
            TinyCsDiagnosticIds.UnsupportedApi));
        Assert.Contains("System.IO.File.ReadAllText", unsupported.Stderr);
    }

    [Fact]
    public void Cli_Check_ComplianceWarnings_ReturnsFailure()
    {
        using var temp = TempDir.Create();
        var inputPath = temp.Write("app.cs", """
            using System.IO;

            public struct Vec2
            {
                public int X;
                public int this[int i] { get { return X; } }
            }

            public class T
            {
                public static string Test()
                {
                    return File.ReadAllText("save.dat");
                }
            }
            """);

        var result = RunCli("check", inputPath);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(TinyCsDiagnosticIds.UnsupportedSyntax, result.Stderr);
        Assert.Contains("StructMember", result.Stderr);
        Assert.Contains(TinyCsDiagnosticIds.UnsupportedApi, result.Stderr);
        Assert.Contains("System.IO.File.ReadAllText", result.Stderr);
    }

    [Fact]
    public void Cli_Check_OrdinaryTypeMismatch_ReturnsCompileError()
    {
        using var temp = TempDir.Create();
        var inputPath = temp.Write("app.cs", """
            public class T
            {
                public static int Test()
                {
                    int value = "oops";
                    return value;
                }
            }
            """);

        var result = RunCli("check", inputPath);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("error CS0029:", result.Stderr);
    }

    [Fact]
    public void Cli_Check_AnalyzerDemoReportsExpectedDiagnostics()
    {
        var inputPath = TestHelper.FindProjectFile(
            "samples/analyzer-demo/Program.cs");

        var result = RunCli("check", inputPath);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(5, CountDiagnostics(result.Stderr,
            TinyCsDiagnosticIds.UnsupportedSyntax));
        Assert.Equal(1, CountDiagnostics(result.Stderr,
            TinyCsDiagnosticIds.UnsupportedApi));
        Assert.Equal(1, CountDiagnostics(result.Stderr,
            TinyCsDiagnosticIds.UnsupportedCollectionNull, severity: "warning"));
        Assert.Contains("StructMember", result.Stderr);
        Assert.Contains("LocalFunctionStatement", result.Stderr);
        Assert.Contains("TryStatement", result.Stderr);
        Assert.Contains("ThrowStatement", result.Stderr);
        Assert.Contains("ListPattern", result.Stderr);
        Assert.Contains("System.IO.File.ReadAllText", result.Stderr);
        Assert.Contains("Dictionary<K,V>", result.Stderr);
    }

    // 名前は C# の識別子のまま IL に出るので、tcs は命名の流儀を判定しない
    [Fact]
    public void Cli_Check_DoesNotJudgeNamingStyle()
    {
        using var temp = TempDir.Create();
        var inputPath = temp.Write("app.cs", """
            public class game
            {
                const int MAX_BALLS = 3;
                public int Score;
                int Lives;
                public static int onInit(int Count)
                {
                    int X = MAX_BALLS + Count;
                    return X;
                }
            }
            """);

        var result = RunCli("check", inputPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
    }

    // 警告 (TCS1003) だけでも check は exit 1。tcs c は警告では止めずに書く
    [Fact]
    public void Cli_Check_FailsOnWarnings_CStillWrites()
    {
        using var temp = TempDir.Create();
        var inputPath = temp.Write("app.cs", """
            public class T
            {
                public static int Test()
                {
                    var names = new string[2];
                    return names.Length;
                }
            }
            """);
        var outputPath = temp.PathFor("app.c");

        var check = RunCli("check", inputPath);
        Assert.Equal(1, check.ExitCode);
        Assert.Contains("TCS1003", check.Stderr);
        Assert.Equal(0, CountDiagnostics(check.Stderr, "TCS1003"));
        Assert.Equal(1, CountDiagnostics(check.Stderr, "TCS1003", "warning"));

        var c = RunCli("c", inputPath, "-o", outputPath);
        Assert.Equal(0, c.ExitCode);
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public void Cli_AtomicOutputReplace_PreservesUnixMode()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var temp = TempDir.Create();
        var inputPath = temp.Write("input.cs", ValidSource);
        var outputPath = temp.Write("output.c", "old output\n");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                   | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                   | UnixFileMode.OtherRead;
        File.SetUnixFileMode(outputPath, mode);

        var result = RunCli("c", inputPath, "-o", outputPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(mode, File.GetUnixFileMode(outputPath));
    }

    [Fact]
    public void Cli_AtomicOutputReplace_RespectsUnixWritePermission()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var temp = TempDir.Create();
        var inputPath = temp.Write("input.cs", ValidSource);
        var outputPath = temp.Write("output.c", "do not overwrite\n");
        var readOnlyMode = UnixFileMode.UserRead | UnixFileMode.GroupRead
                           | UnixFileMode.OtherRead;
        File.SetUnixFileMode(outputPath, readOnlyMode);

        var result = RunCli("c", inputPath, "-o", outputPath);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("do not overwrite\n", File.ReadAllText(outputPath));
        Assert.Equal(readOnlyMode, File.GetUnixFileMode(outputPath));
    }

    [Theory]
    [InlineData("symbolic")]
    [InlineData("hard")]
    public void Cli_UnrelatedOutputLink_IsSafelyDetached(string linkKind)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()
            && !OperatingSystem.IsMacOS()) return;
        if (linkKind == "symbolic" && OperatingSystem.IsWindows()) return;

        using var temp = TempDir.Create();
        var inputPath = temp.Write("input.cs", ValidSource);
        var targetPath = temp.Write("target.c", "keep target\n");
        var outputPath = temp.PathFor("output.c");
        if (linkKind == "symbolic")
            File.CreateSymbolicLink(outputPath, targetPath);
        else
            FileLinkTestHelper.CreateHardLink(outputPath, targetPath);

        var result = RunCli("c", inputPath, "-o", outputPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("keep target\n", File.ReadAllText(targetPath));
        Assert.Null(new FileInfo(outputPath).LinkTarget);
        Assert.Contains("T_Test", File.ReadAllText(outputPath));
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(
        params string[] args) =>
        ConsoleCapture.Run(() => Program.Main(args));

    private static int CountDiagnostics(string text, string diagnosticId,
        string severity = "error") =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains($"{severity} {diagnosticId}:"));

    private const string ValidSource = """
        public static class T
        {
            public static int Test() => 42;
        }
        """;

    private sealed class TempDir : IDisposable
    {
        private readonly string _path;

        private TempDir(string path)
        {
            _path = path;
            Directory.CreateDirectory(_path);
        }

        public static TempDir Create() =>
            new(Path.Combine(Path.GetTempPath(), $"tcs_cli_{Guid.NewGuid():N}"));

        public string PathFor(string fileName) => Path.Combine(_path, fileName);

        public string Write(string fileName, string contents)
        {
            var path = PathFor(fileName);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(_path, true); } catch { }
        }
    }
}
