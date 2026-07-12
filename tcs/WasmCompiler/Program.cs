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

// wasm では reflection ベースの JsonSerializer が無効のため source generator を使う
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CompileRequest))]
[JsonSerializable(typeof(CompileResponse))]
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

    private static void EnsureInit()
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
