using System.Diagnostics;

namespace TinyCs.Tests;

public class WatchModeTests
{
    [Fact]
    public void Watch_OutputCollision_ExitsBeforeInitialBuild()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(),
            $"tcs_watch_collision_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var inputPath = Path.Combine(tmpDir, "test.cs");
        var source = """
            public static class T
            {
                public static int Value() => 1;
            }
            """;
        File.WriteAllText(inputPath, source);

        try
        {
            var psi = CreateTranspilerProcess(
                inputPath, "-o", inputPath, "--watch", "--no-runtime");

            using var proc = Process.Start(psi)!;
            try
            {
                var exited = proc.WaitForExit(5000);
                Assert.True(exited, "watch must reject a colliding output before waiting");
                var stderr = proc.StandardError.ReadToEnd();
                Assert.Equal(1, proc.ExitCode);
                Assert.Contains("conflicts with input", stderr);
                Assert.Equal(source, File.ReadAllText(inputPath));
            }
            finally
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    [Fact]
    public void Watch_HardLinkOutputCollision_ExitsBeforeInitialBuild()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()
            && !OperatingSystem.IsMacOS()) return;

        var tmpDir = Path.Combine(Path.GetTempPath(),
            $"tcs_watch_link_collision_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var inputPath = Path.Combine(tmpDir, "test.cs");
        var outputPath = Path.Combine(tmpDir, "output.lua");
        var source = """
            public static class T
            {
                public static int Value() => 1;
            }
            """;
        File.WriteAllText(inputPath, source);
        FileLinkTestHelper.CreateHardLink(outputPath, inputPath);
        var originalWriteTime = File.GetLastWriteTimeUtc(inputPath);

        try
        {
            var psi = CreateTranspilerProcess(
                inputPath, "-o", outputPath, "--watch", "--no-runtime");

            using var proc = Process.Start(psi)!;
            try
            {
                var exited = proc.WaitForExit(5000);
                Assert.True(exited,
                    "watch must reject a hard-link output before waiting");
                var stderr = proc.StandardError.ReadToEnd();
                Assert.Equal(1, proc.ExitCode);
                Assert.Contains("conflicts with input", stderr);
                Assert.Equal(source, File.ReadAllText(inputPath));
                Assert.Equal(originalWriteTime,
                    File.GetLastWriteTimeUtc(inputPath));
            }
            finally
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    [Fact]
    public void Watch_FileChange_TriggersRebuild()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"tcs_watch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var inputPath = Path.Combine(tmpDir, "test.cs");
        var outputPath = Path.Combine(tmpDir, "test.lua");

        try
        {
            // Write V1
            File.WriteAllText(inputPath, """
                public class Hello
                {
                    public static int Value() { return 1; }
                }
                """);

            // Start tcs --watch
            var psi = CreateTranspilerProcess(
                inputPath, "-o", outputPath, "--watch");

            using var proc = Process.Start(psi)!;

            try
            {
                // Wait for initial build
                WaitForFile(outputPath, timeoutMs: 15000);
                var v1 = File.ReadAllText(outputPath);
                Assert.Contains("Hello", v1);

                // Write V2 (change return value)
                Thread.Sleep(500); // let watcher settle
                File.WriteAllText(inputPath, """
                    public class Hello
                    {
                        public static int Value() { return 42; }
                    }
                    """);

                // Wait for rebuild (output file should change)
                WaitForFileChange(outputPath, v1, timeoutMs: 5000);
                var v2 = File.ReadAllText(outputPath);
                Assert.Contains("42", v2);
            }
            finally
            {
                // dotnet run の子プロセス (Transpiler --watch) ごと止める
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    [Fact]
    public void Watch_RefFileChange_TriggersRebuild()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"tcs_watch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var inputPath = Path.Combine(tmpDir, "test.cs");
        var refPath = Path.Combine(tmpDir, "engine.cs");
        var outputPath = Path.Combine(tmpDir, "test.lua");

        try
        {
            File.WriteAllText(inputPath, """
                using Engine;

                public class T
                {
                    public static int Value() { return Api.Value(); }
                }
                """);
            File.WriteAllText(refPath, """
                namespace Engine;
                public static class Api
                {
                    public static int Value() => default!;
                }
                """);

            var psi = CreateTranspilerProcess(
                inputPath, "--ref", refPath, "-o", outputPath, "--watch");

            using var proc = Process.Start(psi)!;

            try
            {
                WaitForFile(outputPath, timeoutMs: 15000);
                var initialWrite = File.GetLastWriteTimeUtc(outputPath);

                Thread.Sleep(500);
                File.WriteAllText(refPath, """
                    namespace Engine;
                    public static class Api
                    {
                        public static int Value() => default!;
                        public static int Other() => default!;
                    }
                    """);

                WaitForFileWriteAfter(outputPath, initialWrite, timeoutMs: 5000);
            }
            finally
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    private static void WaitForFile(string path, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return;
            Thread.Sleep(100);
        }
        throw new TimeoutException($"File {path} not created within {timeoutMs}ms");
    }

    private static void WaitForFileChange(string path, string oldContent, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(path))
            {
                var current = File.ReadAllText(path);
                if (current != oldContent)
                    return;
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"File {path} did not change within {timeoutMs}ms");
    }

    private static void WaitForFileWriteAfter(string path, DateTime oldWriteTimeUtc,
        int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(path)
                && File.GetLastWriteTimeUtc(path) > oldWriteTimeUtc)
            {
                return;
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"File {path} was not rewritten within {timeoutMs}ms");
    }

    private static ProcessStartInfo CreateTranspilerProcess(
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}
