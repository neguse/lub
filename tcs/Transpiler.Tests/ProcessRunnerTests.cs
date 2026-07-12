using System.Diagnostics;

namespace TinyCs.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public void RunLua_TimeoutKillsProcess()
    {
        TestHelper.RunLua("");
        var stopwatch = Stopwatch.StartNew();

        var error = Assert.Throws<ProcessTimeoutException>(() =>
            TestHelper.RunLua("""
                io.stdout:write("stdout-before-timeout")
                io.stdout:flush()
                io.stderr:write("stderr-before-timeout")
                io.stderr:flush()
                while true do end
                """, TimeSpan.FromMilliseconds(500)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"timeout cleanup took {stopwatch.Elapsed}");
        Assert.Contains("timed out", error.Message);
        Assert.Contains("500 ms", error.Message);
        Assert.Contains("stdout-before-timeout", error.Message);
        Assert.Contains("stderr-before-timeout", error.Message);
        Assert.Contains("while true do end", error.Message);
        AssertProcessExited(error.ProcessId);
    }

    [Fact]
    public void Run_TimeoutKillsDescendantProcess()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(),
            $"tcs-process-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var childScriptPath = Path.Combine(tempDirectory, "child.lua");
        var rootPidPath = Path.Combine(tempDirectory, "root.pid");
        var childPidPath = Path.Combine(tempDirectory, "child.pid");

        try
        {
            File.WriteAllText(childScriptPath, "while true do end");
            var startInfo = CreateProcessTreeStartInfo(
                childScriptPath, rootPidPath, childPidPath);
            var timeout = OperatingSystem.IsWindows()
                ? TimeSpan.FromSeconds(10)
                : TimeSpan.FromSeconds(3);

            var error = Assert.Throws<ProcessTimeoutException>(() =>
                TestProcessRunner.Run(startInfo, timeout,
                    "descendant cleanup test"));

            Assert.True(File.Exists(rootPidPath),
                "root process did not publish its PID before timeout");
            Assert.True(File.Exists(childPidPath),
                "child process did not publish its PID before timeout");
            var rootPid = int.Parse(File.ReadAllText(rootPidPath));
            var childPid = int.Parse(File.ReadAllText(childPidPath));
            Assert.Equal(error.ProcessId, rootPid);
            AssertProcessExited(error.ProcessId);
            AssertProcessExited(childPid);
        }
        finally
        {
            TryTerminateProcessFromFile(rootPidPath);
            TryTerminateProcessFromFile(childPidPath);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void RunLua_DrainsStdoutAndStderrConcurrently()
    {
        const int outputLength = 200_000;
        var output = TestHelper.RunLua($$"""
            io.stdout:write(string.rep("o", {{outputLength}}))
            io.stderr:write(string.rep("e", {{outputLength}}))
            """, TimeSpan.FromSeconds(5));

        Assert.Equal(outputLength, output.Length);
        Assert.Equal(new string('o', outputLength), output);
    }

    private static ProcessStartInfo CreateProcessTreeStartInfo(
        string childScriptPath, string rootPidPath, string childPidPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? "powershell.exe"
                : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["TCS_TEST_LUA"] =
            TestHelper.LuaExecutablePath;
        startInfo.Environment["TCS_TEST_CHILD_SCRIPT"] = childScriptPath;
        startInfo.Environment["TCS_TEST_ROOT_PID_FILE"] = rootPidPath;
        startInfo.Environment["TCS_TEST_PID_FILE"] = childPidPath;

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("""
                Set-Content -LiteralPath $env:TCS_TEST_ROOT_PID_FILE `
                    -Value $PID -NoNewline
                $child = Start-Process `
                    -FilePath $env:TCS_TEST_LUA `
                    -ArgumentList ('"' + $env:TCS_TEST_CHILD_SCRIPT + '"') `
                    -PassThru
                Set-Content -LiteralPath $env:TCS_TEST_PID_FILE `
                    -Value $child.Id -NoNewline
                $child.WaitForExit()
                """);
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("""
                printf '%s' "$$" > "$TCS_TEST_ROOT_PID_FILE"
                "$TCS_TEST_LUA" "$TCS_TEST_CHILD_SCRIPT" &
                child=$!
                printf '%s' "$child" > "$TCS_TEST_PID_FILE"
                wait "$child"
                """);
        }
        return startInfo;
    }

    private static void AssertProcessExited(int processId)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return;
            }
            catch (ArgumentException)
            {
                return;
            }
            Thread.Sleep(25);
        }
        Assert.Fail($"Process {processId} remained after timeout cleanup.");
    }

    private static void TryTerminateProcessFromFile(string pidPath)
    {
        try
        {
            if (!File.Exists(pidPath)
                || !int.TryParse(File.ReadAllText(pidPath),
                    out var processId))
            {
                return;
            }
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(milliseconds: 5_000);
        }
        catch (Exception ex) when (ex is SystemException
            or AggregateException)
        {
            // Best-effort safety net for a failed process-cleanup assertion.
        }
    }
}
