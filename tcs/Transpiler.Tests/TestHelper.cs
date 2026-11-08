using System.Diagnostics;

namespace TinyCs.Tests;

public static class TestHelper
{
    private static readonly TimeSpan LuaProcessTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LuaVersionTimeout = TimeSpan.FromSeconds(5);
    private static readonly string ProjectRoot = FindProjectRoot();
    internal static string ProjectRootPath => ProjectRoot;
    private static readonly string LuaPath = FindLua();
    internal static string LuaExecutablePath => LuaPath;
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
        // 数値モデルの正本は i32/f32 (il-spec §5-6)。LUA_32BITS
        // ビルド (lua32) を優先し、無ければ 64bit lua へ fallback する
        var names = isWindows
            ? new[] { "lua32.exe", "lua.exe", "lua" }
            : new[] { "lua32", "lua", "lua5.5" };

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
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-v");
            var result = TestProcessRunner.Run(psi, LuaVersionTimeout);
            var version = (result.Stdout + result.Stderr).Trim();
            if (result.ExitCode != 0 || version.Length == 0)
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
    /// C# の program に `Console.WriteLine(csharpExpr)` の entry を足して tcs VM で走らせ、stdout を返す (VmCorpus.Run)。
    /// TCS_DIFFERENTIAL=1 なら実 .NET の stdout とも一致を要求する。dotnet: false は .NET と意図して違う program (理由は呼び出し側に書く)。
    /// </summary>
    public static string CompileAndRunVm(string csharpSource, string csharpExpr, bool dotnet = true) =>
        VmCorpus.Run(csharpSource, csharpExpr, dotnet);

    /// <summary>複数の file の program の CompileAndRunVm。</summary>
    public static string CompileAndRunVm(string[] csharpSources, string csharpExpr, bool dotnet = true) =>
        VmCorpus.Run(csharpSources, csharpExpr, dotnet);

    /// <summary>本文が mainBody の entry (`TcsEntry.Main`) を足して tcs VM で走らせる。本文が `Console.WriteLine` で結果を書く。</summary>
    public static string CompileAndRunVmMain(string csharpSource, string mainBody, bool dotnet = true) =>
        VmCorpus.RunMain(csharpSource, mainBody, dotnet);

    /// <summary>program 全体を tcs VM で走らせた stdout。entryClass は static Main を持つ class (null なら top-level 文)。
    /// TCS_DIFFERENTIAL=1 なら実 .NET の stdout とも一致を要求する。</summary>
    public static string CompileAndRunVmProgram(string[] sources, string? entryClass = null, bool dotnet = true) =>
        VmCorpus.RunWhole(sources, entryClass, dotnet);

    /// <summary>top-level 文の program: 最後の top-level 文の後で `Console.WriteLine(csharpExpr)` を走らせる (式は top-level の local を読める)。</summary>
    public static string CompileAndRunVmTopLevel(string csharpSource, string csharpExpr, bool dotnet = true) =>
        VmCorpus.RunTopLevel(csharpSource, csharpExpr, dotnet);

    /// <summary>
    /// Lua backend: C# を runtime 付きの Lua にし、Lua の式 luaExpr を print した stdout を返す。Lua の出力と Lua の runtime を見るテストだけが使う。
    /// </summary>
    public static string TranspileAndRunWithRuntime(string csharpSource,
        string luaExpr, TimeSpan? timeout = null)
    {
        var lua = Transpiler.Transpile(csharpSource);
        var runtimePath = FindProjectFile("runtime/tinysystem.lua");
        var script = $"local TinySystem = dofile(\"{runtimePath}\")\n" +
                     "List = TinySystem.List\n" +
                     "Dict = TinySystem.Dict\n" +
                     "Math = TinySystem.Math\n" +
                     "String = TinySystem.String\n" +
                     "Random = TinySystem.Random\n" +
                     "Char = TinySystem.Char\n" +
                     $"{lua}\nprint({luaExpr})";
        return RunLua(script, timeout).Trim();
    }

    /// <summary>
    /// Lua backend: C# を Lua にし、Lua の式 luaExpr を print した stdout を返す。Lua の出力を見るテストだけが使う。
    /// luaExpr は emit された Lua の名前で書く (user の member は C# の識別子のまま、Lua の予約語だけ `end_`)。
    /// </summary>
    public static string TranspileAndRun(string csharpSource, string luaExpr)
    {
        var lua = Transpiler.Transpile(csharpSource);
        return RunLua($"{lua}\nprint({luaExpr})").Trim();
    }

    /// <summary>
    /// hot reload の実 VM 手順を再現する script: v1 chunk と reload chunk を
    /// それぞれ別 chunk として load() し (chunk-local は共有されない)、
    /// state / asserts は呼び出し側の chunk で走らせる。
    /// </summary>
    public static string ComposeReload(string v1Source, string stateLua,
        string v2Source, string assertsLua) =>
        $"{LoadChunk(Transpiler.Transpile([v1Source], instanceRegistry: true), "v1")}\n{stateLua}\n" +
        $"{LoadChunk(HotReload.EmitReloadChunk([v1Source], [v2Source]), "reload")}\n" +
        assertsLua;

    public static string LoadChunk(string lua, string name)
    {
        var eq = "=";
        while (lua.Contains($"]{eq}]", StringComparison.Ordinal)) eq += "=";
        return $"assert(load([{eq}[\n{lua}]{eq}], \"={name}\"))()";
    }

    /// <summary>
    /// Run a Lua script string and return stdout.
    /// </summary>
    public static string RunLua(string script, TimeSpan? timeout = null)
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, script);
            var psi = new ProcessStartInfo
            {
                FileName = LuaPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(tmpFile);
            var result = TestProcessRunner.Run(
                psi, timeout ?? LuaProcessTimeout, script);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Lua exited with code {result.ExitCode}:\n{result.Stderr}\n--- script ---\n{script}");
            return result.Stdout;
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }
}
