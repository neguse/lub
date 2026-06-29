using System.Diagnostics;

namespace TinyCs.Tests;

public static class TestHelper
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string LuaPath = FindLua();
    public static string LuaVersion => GetLuaVersion(LuaPath);

    private static string FindProjectRoot()
    {
        // Try multiple starting points
        string?[] starts = [
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(TestHelper).Assembly.Location),
            Environment.CurrentDirectory
        ];

        foreach (var start in starts)
        {
            if (string.IsNullOrEmpty(start)) continue;
            string? dir = start;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "tcs.slnx"))) return dir;
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
        }
        return Environment.CurrentDirectory;
    }

    private static string FindLua()
    {
        // Try platform-specific binary names
        var isWindows = OperatingSystem.IsWindows();
        var names = isWindows
            ? new[] { "lua.exe", "lua" }
            : new[] { "lua", "lua5.5" };

        // Check local build first
        foreach (var name in names)
        {
            var candidate = Path.Combine(ProjectRoot, "deps", "lua", name);
            if (File.Exists(candidate))
            {
                EnsureLua55(candidate);
                return candidate;
            }
        }

        // Fallback to system PATH
        var fallback = isWindows ? "lua.exe" : "lua";
        EnsureLua55(fallback);
        return fallback;
    }

    private static void EnsureLua55(string luaPath)
    {
        var version = GetLuaVersion(luaPath);
        if (!version.StartsWith("Lua 5.5", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"TinyC# tests require Lua 5.5, but '{luaPath}' is '{version}'.");
        }
    }

    private static string GetLuaVersion(string luaPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = luaPath,
                Arguments = "-v",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException(
                    $"Failed to start Lua: {luaPath}");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            var version = (stdout + stderr).Trim();
            if (proc.ExitCode != 0 || version.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Failed to read Lua version from '{luaPath}'.");
            }

            return version;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Lua 5.5 binary not found or not executable: {luaPath}", ex);
        }
    }

    /// <summary>
    /// Resolve a path relative to the project root.
    /// </summary>
    public static string FindProjectFile(string relativePath)
    {
        var path = Path.Combine(ProjectRoot, relativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Project file not found: {path}");
        return path.Replace("\\", "/"); // Lua needs forward slashes
    }

    /// <summary>
    /// Transpile C# source with runtime loaded, wrap with a Lua expression to evaluate, run in Lua VM.
    /// </summary>
    public static string TranspileAndRunWithRuntime(string csharpSource, string luaExpr)
    {
        var lua = Transpiler.Transpile(csharpSource);
        var runtimePath = FindProjectFile("runtime/tinysystem.lua");
        var script = $"local TinySystem = dofile(\"{runtimePath}\")\n" +
                     "List = TinySystem.List\n" +
                     "Dict = TinySystem.Dict\n" +
                     "Math = TinySystem.Math\n" +
                     "String = TinySystem.String\n" +
                     "Random = TinySystem.Random\n" +
                     $"{lua}\nprint({luaExpr})";
        return RunLua(script).Trim();
    }

    /// <summary>
    /// Transpile C# source, wrap with a Lua expression to evaluate, run in Lua VM, return stdout.
    /// </summary>
    public static string TranspileAndRun(string csharpSource, string luaExpr)
    {
        var lua = Transpiler.Transpile(csharpSource);
        var script = $"{lua}\nprint({luaExpr})";
        return RunLua(script).Trim();
    }

    /// <summary>
    /// Run a Lua script string and return stdout.
    /// </summary>
    public static string RunLua(string script)
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, script);
            var psi = new ProcessStartInfo
            {
                FileName = LuaPath,
                Arguments = tmpFile,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start Lua: {LuaPath}");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"Lua exited with code {proc.ExitCode}:\n{stderr}\n--- script ---\n{script}");
            return stdout;
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }
}
