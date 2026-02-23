using System.Diagnostics;

namespace TinyCs.Tests;

public static class TestHelper
{
    private static readonly string LuaPath = FindLua();

    private static string FindLua()
    {
        // Look for deps/lua/lua relative to the solution root
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "deps", "lua", "lua");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        // Fallback: assume it's on PATH
        return "lua";
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
