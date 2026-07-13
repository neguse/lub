// browser-wasm 上で tcs Transpiler を JS へ公開する entry。
// JS 側は dotnet.create() → getAssemblyExports("WasmCompiler") →
// CompilerExports.Compile(requestJson) を呼ぶ。
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using TinyCs;

// Main はランタイム常駐のためだけに待機する (実処理は JSExport 側)。
await Task.Delay(Timeout.Infinite);

public sealed class CompileRequest
{
    /// <summary>transpile 対象 (path -> source)。</summary>
    public Dictionary<string, string> Files { get; set; } = [];

    /// <summary>型チェック専用の参照ソース (--ref 相当、Lua 出力なし)。</summary>
    public Dictionary<string, string>? Refs { get; set; }

    /// <summary>--entry 相当。出力末尾に `return <Class>` を足す。</summary>
    public string? EntryClass { get; set; }

    /// <summary>--no-naming-check 相当 (false で naming 警告を抑制)。</summary>
    public bool CheckNaming { get; set; } = true;
}

public sealed class CompileResponse
{
    public bool Ok { get; set; }

    /// <summary>成功時のみ。TinySystem runtime prelude 込みの完全な Lua。</summary>
    public string? Lua { get; set; }

    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

/// <summary>SessionExports.Update の応答 (M1 gate 計測用の最小 wire)。</summary>
public sealed class SessionUpdateResponse
{
    public bool Ok { get; set; }
    public bool FastPath { get; set; }
    public List<string> Errors { get; set; } = [];
    public long ParseUpdateMs { get; set; }
    public long DiagnosticsMs { get; set; }
    public long ComplianceMs { get; set; }
    public long EmitMs { get; set; }
    public int ParsedTreeCount { get; set; }
    public int EmittedModuleCount { get; set; }
    public int ArtifactBytes { get; set; }
}

// wasm では reflection ベースの JsonSerializer が無効のため source generator を使う
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CompileRequest))]
[JsonSerializable(typeof(CompileResponse))]
[JsonSerializable(typeof(SessionUpdateResponse))]
public sealed partial class CompileJsonContext : JsonSerializerContext;

public static partial class CompilerExports
{
    private static bool _initialized;
    private static string? _runtimeLua;

    private static Stream Res(string name) =>
        typeof(CompilerExports).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException("missing resource: " + name);

    private static MetadataReference Ref(string name) =>
        MetadataReference.CreateFromStream(Res(name));

    internal static void EnsureInit()
    {
        if (_initialized)
            return;
        // Assembly.Location が空の host なので、埋め込んだ参照アセンブリを
        // byte image で Transpiler へ注入する。
        Transpiler.References =
        [
            Ref("refs/System.Runtime.dll"),
            Ref("refs/System.Collections.dll"),
            Ref("refs/System.Linq.dll"),
            Ref("refs/System.Console.dll"),
            Ref("refs/TinySystem.dll"),
        ];
        using var reader = new StreamReader(Res("runtime/tinysystem.lua"));
        _runtimeLua = reader.ReadToEnd();
        _initialized = true;
    }

    [JSExport]
    public static string Compile(string requestJson)
    {
        try
        {
            EnsureInit();
            var req = JsonSerializer.Deserialize(
                          requestJson, CompileJsonContext.Default.CompileRequest)
                      ?? throw new InvalidOperationException("empty request");
            var files = req.Files.ToArray();
            var refs = req.Refs?.Values.ToArray();
            var result = Transpiler.TranspileWithDiagnostics(
                [.. files.Select(kv => kv.Value)],
                [.. files.Select(kv => kv.Key)],
                refs, req.EntryClass, req.CheckNaming);
            string? lua = null;
            if (result.Success)
                lua = LuaRuntime.CreateEmbeddedPrelude(_runtimeLua!) + result.Lua;
            return JsonSerializer.Serialize(new CompileResponse
            {
                Ok = result.Success,
                Lua = lua,
                Errors = result.Errors,
                Warnings = result.Warnings,
            }, CompileJsonContext.Default.CompileResponse);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new CompileResponse
            {
                Ok = false,
                Errors = [ex.ToString()],
            }, CompileJsonContext.Default.CompileResponse);
        }
    }
}

// M1 gate (design doc §17) 計測用の最小 session API。single session 固定。
// production wire contract (projectEpoch / LinkSnapshot / TextChange span 等、
// design doc §13) は M4 で確定するため、ここでは意図的に持たない。
public static partial class SessionExports
{
    private static IncrementalCompilationSession? _session;

    [JSExport]
    public static string Open(string requestJson)
    {
        try
        {
            CompilerExports.EnsureInit();
            var req = JsonSerializer.Deserialize(
                          requestJson, CompileJsonContext.Default.CompileRequest)
                      ?? throw new InvalidOperationException("empty request");
            _session = new IncrementalCompilationSession(
                req.Refs?.Values.ToArray(), req.CheckNaming);
            _session.OpenProject([.. req.Files.Select(kv => (kv.Key, kv.Value))]);
            var (errors, _) = _session.CollectDiagnostics();
            return JsonSerializer.Serialize(new CompileResponse
            {
                Ok = errors.Count == 0,
                Errors = errors,
            }, CompileJsonContext.Default.CompileResponse);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new CompileResponse
            {
                Ok = false,
                Errors = [ex.ToString()],
            }, CompileJsonContext.Default.CompileResponse);
        }
    }

    [JSExport]
    public static string Update(string path, string content)
    {
        try
        {
            var session = _session
                ?? throw new InvalidOperationException("SessionExports.Open first");
            var r = session.Update(path, content);
            return JsonSerializer.Serialize(new SessionUpdateResponse
            {
                Ok = r.Success,
                FastPath = r.FastPath,
                Errors = r.Errors,
                ParseUpdateMs = r.ParseUpdateMs,
                DiagnosticsMs = r.DiagnosticsMs,
                ComplianceMs = r.ComplianceMs,
                EmitMs = r.EmitMs,
                ParsedTreeCount = r.ParsedTreeCount,
                EmittedModuleCount = r.EmittedModuleCount,
                ArtifactBytes = r.ChangedArtifacts.Sum(a => a.Lua.Length),
            }, CompileJsonContext.Default.SessionUpdateResponse);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new SessionUpdateResponse
            {
                Ok = false,
                Errors = [ex.ToString()],
            }, CompileJsonContext.Default.SessionUpdateResponse);
        }
    }
}
