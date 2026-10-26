using System.Diagnostics;

namespace TinyCs.Tests;

public static class TestHelper
{
    private static readonly TimeSpan LuaProcessTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LuaVersionTimeout = TimeSpan.FromSeconds(5);
    private static readonly string ProjectRoot = FindProjectRoot();
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
    /// Transpile C# source with runtime loaded, wrap with a Lua expression to evaluate, run in Lua VM.
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
        var result = RunLua(script, timeout).Trim();
        SpecConformance.CorpusDifferential.Check(csharpSource, luaExpr, result);
        return result;
    }

    /// <summary>
    /// Transpile C# source, wrap with a Lua expression to evaluate, run in Lua VM, return stdout.
    /// luaExpr は emit された Lua の名前で書く (user の member は C# の識別子のまま、
    /// Lua の予約語だけ `end_`)。
    /// </summary>
    /// <param name="differential">false で dotnet differential を対象外にする
    /// (文字列長 UTF-16 vs byte など既知の意味論差を検証するテスト専用)</param>
    public static string TranspileAndRun(string csharpSource, string luaExpr,
        bool differential = true)
    {
        var lua = Transpiler.Transpile(csharpSource);
        var script = $"{lua}\nprint({luaExpr})";
        var result = RunLua(script).Trim();
        if (differential)
            SpecConformance.CorpusDifferential.Check(csharpSource, luaExpr,
                result);
        SessionDifferential.Check(csharpSource, luaExpr, result);
        return result;
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

    /// <summary>
    /// ComposeReload の registry 版: v1 を instance registry 付きの session の snapshot で
    /// 起こし、v2 を session の Update と変わった module だけの batch (instance の移行込み)
    /// で当てる。state / asserts は registry の module env を親にした env で走らせる
    /// </summary>
    public static string ComposeRegistryReload(string v1Source, string stateLua,
        string v2Source, string assertsLua)
    {
        var session = new IncrementalCompilationSession(instanceRegistry: true);
        session.OpenProject([("a.cs", v1Source)]);
        var baseRevision = session.Revision;
        var snapshot = ModuleLinker.LinkSnapshot(session.Artifacts, baseRevision, null,
            LuaRuntime.LoadTinySystemSource(),
            LuaRuntime.LoadRuntimeFile(LuaRuntime.RegistryRelativePath));
        var update = session.Update("a.cs", v2Source);
        if (!update.Success)
            throw new InvalidOperationException(string.Join("\n", update.Errors));
        if (update.RequiresRestart)
            throw new InvalidOperationException(
                "restart required: " + string.Join("; ", update.RestartReasons));
        var batch = ModuleLinker.LinkBatch(session.Artifacts,
            [.. update.ChangedArtifacts.Select(a => a.ModuleId)], session.Revision,
            baseRevision, null, migration: session.MigrationFrom(baseRevision));
        // state と asserts は同じ chunk の local を共有する (ComposeReload と同じ)
        return $"{LoadChunk(snapshot, "snapshot")}\n" + LoadChunk(
            "local _ENV = setmetatable({}, { __index = __tcs_module_runtime.registry.env })\n"
            + $"{stateLua}\n{LoadChunk(batch, "batch")}\n{assertsLua}", "reload");
    }

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

/// <summary>
/// TCS_SESSION_DIFFERENTIAL=1 で、TranspileAndRun の入力を増分 session の snapshot
/// (ModuleRegistry で当てる) でも動かし、全体の変換と同じ結果になることを確かめる。
/// luaExpr は registry の module env (型の alias → host の global) で評価する
/// </summary>
internal static class SessionDifferential
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("TCS_SESSION_DIFFERENTIAL") == "1";

    public static void Check(string csharpSource, string luaExpr, string fullResult)
    {
        if (!Enabled)
            return;
        // module mode は top-level 文を define の中で走らせ、その local は luaExpr から見えない
        if (Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(csharpSource).GetRoot()
                .ChildNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax>().Any())
            return;
        var session = new IncrementalCompilationSession();
        session.OpenProject([("a.cs", csharpSource)]);
        var (errors, _) = session.CollectDiagnostics();
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "session differential: session failed where full build succeeded\n"
                + string.Join("\n", errors));
        var snapshot = ModuleLinker.LinkSnapshot(session.Artifacts, session.Revision, null,
            LuaRuntime.LoadTinySystemSource(),
            LuaRuntime.LoadRuntimeFile(LuaRuntime.RegistryRelativePath));
        var script = $"{TestHelper.LoadChunk(snapshot, "snapshot")}\n"
            + "local _ENV = __tcs_module_runtime.registry.env\n"
            + $"print({luaExpr})";
        var sessionResult = TestHelper.RunLua(script).Trim();
        if (sessionResult != fullResult)
            throw new InvalidOperationException(
                "session differential mismatch\n"
                + $"expr:    {luaExpr}\n"
                + $"full:    {fullResult}\n"
                + $"session: {sessionResult}");
    }
}
