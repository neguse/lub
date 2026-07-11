using System.Diagnostics;

namespace TinyCs.Tests;

public class WatchModeTests
{
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
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project Transpiler -- \"{inputPath}\" -o \"{outputPath}\" --watch",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = FindProjectRoot()
            };

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

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project Transpiler -- \"{inputPath}\" --ref \"{refPath}\" -o \"{outputPath}\" --watch",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = FindProjectRoot()
            };

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

    private static string FindProjectRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "tcs.slnx"))) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return Environment.CurrentDirectory;
    }
}
