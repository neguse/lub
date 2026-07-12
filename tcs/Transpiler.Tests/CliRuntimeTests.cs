using System.Text.Json;

namespace TinyCs.Tests;

[Collection(ConsoleCollection.Name)]
public class CliRuntimeTests
{
    [Fact]
    public void Cli_Help_PrintsUsage()
    {
        var result = RunCli("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage: tcs", result.Stdout);
        Assert.Contains("--map-stacktrace", result.Stdout);
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

    [Fact]
    public void Cli_UnknownOption_ReturnsError()
    {
        var result = RunCli("--unknown");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("unknown option: --unknown", result.Stderr);
    }

    [Fact]
    public void Cli_OutputOptionMissingValue_ReturnsError()
    {
        var result = RunCli("input.cs", "-o", "--watch");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("missing value for -o", result.Stderr);
    }

    [Fact]
    public void Cli_RefOptionMissingValue_ReturnsError()
    {
        var result = RunCli("input.cs", "--ref");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("missing value for --ref", result.Stderr);
    }

    [Fact]
    public void Cli_Check_ValidCode_ReturnsSuccessWithoutLuaOutput()
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

    [Fact]
    public void Cli_Check_ComplianceWarnings_ReturnsFailure()
    {
        using var temp = TempDir.Create();
        var inputPath = temp.Write("app.cs", """
            using System.IO;

            public struct Vec2
            {
                public int X;
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
        Assert.Contains("StructDeclaration", result.Stderr);
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
            TinyCsDiagnosticIds.UnsupportedCollectionNull));
        Assert.Contains("StructDeclaration", result.Stderr);
        Assert.Contains("LocalFunctionStatement", result.Stderr);
        Assert.Contains("TryStatement", result.Stderr);
        Assert.Contains("ThrowStatement", result.Stderr);
        Assert.Contains("ListPattern", result.Stderr);
        Assert.Contains("System.IO.File.ReadAllText", result.Stderr);
        Assert.Contains("List<T>", result.Stderr);
    }

    [Fact]
    public void Cli_EmbedsRuntimeByDefault()
    {
        using var temp = TempDir.Create();
        var inputPath = temp.Write("app.cs", """
            using System.Collections.Generic;

            public class T
            {
                public static int Test()
                {
                    var values = new List<int> { 1, 2 };
                    values.Add(3);
                    return values.Count;
                }
            }
            """);
        var outputPath = temp.PathFor("app.lua");

        var exitCode = Program.Main([inputPath, "-o", outputPath]);

        Assert.Equal(0, exitCode);
        var lua = File.ReadAllText(outputPath);
        Assert.Contains("TinyC# embedded runtime prelude", lua);
        Assert.Equal("3", TestHelper.RunLua($"{lua}\nprint(T.Test())").Trim());
    }

    [Fact]
    public void Cli_NoRuntimeOmitsEmbeddedRuntime()
    {
        using var temp = TempDir.Create();
        var inputPath = temp.Write("app.cs", """
            public class T
            {
                public static int Test() => 42;
            }
            """);
        var outputPath = temp.PathFor("app.lua");

        var exitCode = Program.Main([inputPath, "-o", outputPath, "--no-runtime"]);

        Assert.Equal(0, exitCode);
        var lua = File.ReadAllText(outputPath);
        Assert.DoesNotContain("TinyC# embedded runtime prelude", lua);
        Assert.Equal("42", TestHelper.RunLua($"{lua}\nprint(T.Test())").Trim());
    }

    [Fact]
    public void Cli_SourceMapLinesIncludeRuntimePreludeOffset()
    {
        using var temp = TempDir.Create();
        var inputPath = temp.Write("app.cs", """
            public class T
            {
                public static int Test() => 7;
            }
            """);
        var outputPath = temp.PathFor("app.lua");

        var exitCode = Program.Main([inputPath, "-o", outputPath, "--sourcemap"]);

        Assert.Equal(0, exitCode);
        var luaLines = File.ReadAllLines(outputPath);
        var classLine = Array.FindIndex(luaLines, line => line.Contains("T = {}")) + 1;
        Assert.True(classLine > 1);

        using var json = JsonDocument.Parse(File.ReadAllText(outputPath + ".map"));
        var mappings = json.RootElement.GetProperty("mappings");
        Assert.True(mappings.TryGetProperty(classLine.ToString(), out var entry));
        Assert.Equal(inputPath, entry.GetProperty("file").GetString());
        Assert.Equal(1, entry.GetProperty("line").GetInt32());
    }

    [Theory]
    [InlineData("input", false)]
    [InlineData("ref", false)]
    [InlineData("prelude", false)]
    [InlineData("input", true)]
    [InlineData("ref", true)]
    [InlineData("prelude", true)]
    public void Cli_OutputPathCollision_DoesNotModifyProtectedFile(
        string protectedRole, bool sourceMap)
    {
        using var temp = TempDir.Create();
        var mapSuffix = sourceMap ? ".map" : "";
        var inputPath = temp.Write(
            protectedRole == "input" ? $"input.cs{mapSuffix}" : "input.cs",
            ValidSource);
        var refPath = temp.Write(
            protectedRole == "ref" ? $"ref.cs{mapSuffix}" : "ref.cs",
            ValidRefSource);
        var preludePath = temp.Write(
            protectedRole == "prelude" ? $"prelude.lua{mapSuffix}" : "prelude.lua",
            "-- user prelude\n");
        var protectedPath = protectedRole switch
        {
            "input" => inputPath,
            "ref" => refPath,
            "prelude" => preludePath,
            _ => throw new ArgumentOutOfRangeException(nameof(protectedRole))
        };
        var outputPath = sourceMap
            ? protectedPath[..^".map".Length]
            : protectedPath;

        File.SetLastWriteTimeUtc(protectedPath,
            new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        var originalContent = File.ReadAllText(protectedPath);
        var originalWriteTime = File.GetLastWriteTimeUtc(protectedPath);

        var args = new List<string>
        {
            inputPath,
            "--ref", refPath,
            "--prelude", preludePath,
            "-o", outputPath,
            "--no-runtime"
        };
        if (sourceMap) args.Add("--sourcemap");

        var result = RunCli(args.ToArray());

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("conflicts with", result.Stderr);
        Assert.Contains(protectedRole, result.Stderr);
        Assert.Equal(originalContent, File.ReadAllText(protectedPath));
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(protectedPath));
        if (sourceMap)
            Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void Cli_OutputPathCollision_NormalizesDotSegments()
    {
        using var temp = TempDir.Create();
        var inputPath = temp.Write("input.cs", ValidSource);
        var subDir = temp.PathFor("sub");
        Directory.CreateDirectory(subDir);
        var outputPath = Path.Combine(subDir, "..", "input.cs");
        var originalContent = File.ReadAllText(inputPath);

        var result = RunCli(inputPath, "-o", outputPath, "--no-runtime");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("conflicts with input", result.Stderr);
        Assert.Equal(originalContent, File.ReadAllText(inputPath));
    }

    [Fact]
    public void Cli_OutputPathCollision_UsesPlatformCaseSensitivity()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()
            && !OperatingSystem.IsMacOS()) return;

        using var temp = TempDir.Create();
        var inputPath = temp.Write("CaseSensitive.cs", ValidSource);
        var outputPath = temp.PathFor("casesensitive.cs");
        var originalContent = File.ReadAllText(inputPath);
        var aliasesOnFileSystem = File.Exists(outputPath);

        var result = RunCli(inputPath, "-o", outputPath, "--no-runtime");

        if (OperatingSystem.IsWindows() || aliasesOnFileSystem)
        {
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("conflicts with input", result.Stderr);
            Assert.Equal(originalContent, File.ReadAllText(inputPath));
        }
        else
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(originalContent, File.ReadAllText(inputPath));
            Assert.True(File.Exists(outputPath));
        }
    }

    [Theory]
    [InlineData("symbolic", "input", false)]
    [InlineData("hard", "input", false)]
    [InlineData("symbolic", "ref", true)]
    [InlineData("hard", "ref", true)]
    public void Cli_OutputFileAliasCollision_DoesNotModifyProtectedFile(
        string linkKind, string protectedRole, bool sourceMap)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()
            && !OperatingSystem.IsMacOS()) return;
        if (linkKind == "symbolic" && OperatingSystem.IsWindows()) return;

        using var temp = TempDir.Create();
        var inputPath = temp.Write("input.cs", ValidSource);
        var refPath = temp.Write("ref.cs", ValidRefSource);
        var protectedPath = protectedRole == "input" ? inputPath : refPath;
        var outputPath = temp.PathFor("output.lua");
        var writePath = sourceMap ? outputPath + ".map" : outputPath;

        if (linkKind == "symbolic")
            File.CreateSymbolicLink(writePath, protectedPath);
        else
            FileLinkTestHelper.CreateHardLink(writePath, protectedPath);

        File.SetLastWriteTimeUtc(protectedPath,
            new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        var originalContent = File.ReadAllText(protectedPath);
        var originalWriteTime = File.GetLastWriteTimeUtc(protectedPath);

        var args = new List<string>
        {
            inputPath, "--ref", refPath, "-o", outputPath, "--no-runtime"
        };
        if (sourceMap) args.Add("--sourcemap");

        var result = RunCli(args.ToArray());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"conflicts with {protectedRole}", result.Stderr);
        Assert.Equal(originalContent, File.ReadAllText(protectedPath));
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(protectedPath));
        if (sourceMap)
            Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void Cli_OutputPathCollision_ResolvesParentDirectorySymbolicLink()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var temp = TempDir.Create();
        var realDir = temp.PathFor("real");
        Directory.CreateDirectory(realDir);
        var inputPath = Path.Combine(realDir, "input.cs");
        File.WriteAllText(inputPath, ValidSource);
        var aliasDir = temp.PathFor("alias");
        Directory.CreateSymbolicLink(aliasDir, realDir);
        var outputPath = Path.Combine(aliasDir, "input.cs");
        var originalContent = File.ReadAllText(inputPath);

        var result = RunCli(inputPath, "-o", outputPath, "--no-runtime");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("conflicts with input", result.Stderr);
        Assert.Equal(originalContent, File.ReadAllText(inputPath));
    }

    [Fact]
    public void Cli_AtomicOutputReplace_PreservesUnixMode()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var temp = TempDir.Create();
        var inputPath = temp.Write("input.cs", ValidSource);
        var outputPath = temp.Write("output.lua", "old output\n");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                   | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                   | UnixFileMode.OtherRead;
        File.SetUnixFileMode(outputPath, mode);

        var result = RunCli(inputPath, "-o", outputPath, "--no-runtime");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(mode, File.GetUnixFileMode(outputPath));
    }

    [Fact]
    public void Cli_AtomicOutputReplace_RespectsUnixWritePermission()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var temp = TempDir.Create();
        var inputPath = temp.Write("input.cs", ValidSource);
        var outputPath = temp.Write("output.lua", "do not overwrite\n");
        var readOnlyMode = UnixFileMode.UserRead | UnixFileMode.GroupRead
                           | UnixFileMode.OtherRead;
        File.SetUnixFileMode(outputPath, readOnlyMode);

        var result = RunCli(inputPath, "-o", outputPath, "--no-runtime");

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
        var targetPath = temp.Write("target.lua", "keep target\n");
        var outputPath = temp.PathFor("output.lua");
        if (linkKind == "symbolic")
            File.CreateSymbolicLink(outputPath, targetPath);
        else
            FileLinkTestHelper.CreateHardLink(outputPath, targetPath);

        var result = RunCli(inputPath, "-o", outputPath, "--no-runtime");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("keep target\n", File.ReadAllText(targetPath));
        Assert.Null(new FileInfo(outputPath).LinkTarget);
        Assert.Contains("Generated by TinyC#", File.ReadAllText(outputPath));
    }

    [Fact]
    public void Cli_MapStackTrace_AnnotatesLuaLines()
    {
        using var temp = TempDir.Create();
        var mapPath = temp.Write("app.lua.map", """
            {
              "version": 1,
              "mappings": {
                "42": {"file": "app.cs", "line": 9}
              }
            }
            """);
        var tracePath = temp.Write("trace.txt", """
            app.lua:42: attempt to call a nil value
            stack traceback:
                app.lua:43: in function 'T.Test'
            """);

        var oldOut = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            var exitCode = Program.Main(["--map-stacktrace", mapPath, tracePath]);

            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(oldOut);
        }

        var output = writer.ToString();
        Assert.Contains("app.lua:42: attempt to call a nil value  --> app.cs:9",
            output);
        Assert.Contains("app.lua:43: in function 'T.Test'  --> app.cs:9",
            output);
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

    private static int CountDiagnostics(string text, string diagnosticId) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains($"warning {diagnosticId}:"));

    private const string ValidSource = """
        public static class T
        {
            public static int Test() => 42;
        }
        """;

    private const string ValidRefSource = """
        namespace Host;
        public static class Api
        {
            public static int Value() => default;
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
