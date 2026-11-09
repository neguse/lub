// browser-wasm 上で tcs の .NET の道具を JS へ公開する entry。
// JS 側は dotnet.create() → getAssemblyExports("WasmCompiler") → SessionExports (補完、hover、整形) と
// VmExports (tcs VM の増分 session) を呼ぶ。
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
    /// <summary>入力 (path -> source)。</summary>
    public Dictionary<string, string> Files { get; set; } = [];

    /// <summary>型チェック専用の参照ソース (--ref 相当)。</summary>
    public Dictionary<string, string>? Refs { get; set; }
}

/// <summary>SessionExports.OpenEditor の応答。</summary>
public sealed class SessionOpenResponse
{
    public bool Ok { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public int Epoch { get; set; }
    public int Revision { get; set; }
}

/// <summary>SessionExports.Complete の応答 (T230)。</summary>
public sealed class CompleteResponse
{
    public bool Ok { get; set; }
    public int Epoch { get; set; }
    public List<CompletionItem> Items { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>SessionExports.Hover の応答 (T230)。</summary>
public sealed class HoverResponse
{
    public bool Ok { get; set; }
    public int Epoch { get; set; }
    public bool Found { get; set; }
    public string? Display { get; set; }
    public string? Doc { get; set; }
    public int Start { get; set; }
    public int End { get; set; }
    public string? Error { get; set; }
}

// wasm では reflection ベースの JsonSerializer が無効のため source generator を使う
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CompileRequest))]
[JsonSerializable(typeof(SessionOpenResponse))]
[JsonSerializable(typeof(CompleteResponse))]
[JsonSerializable(typeof(HoverResponse))]
public sealed partial class CompileJsonContext : JsonSerializerContext;

public static partial class CompilerExports
{
    private static bool _referencesInjected;

    private static Stream Res(string name) =>
        typeof(CompilerExports).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException("missing resource: " + name);

    private static MetadataReference Ref(string name) =>
        MetadataReference.CreateFromStream(Res(name));

    // Roslyn の compilation (補完と hover) が読む参照アセンブリ。Assembly.Location が空の host なので、
    // 埋め込んだ参照アセンブリを byte image で注入する。
    internal static void EnsureReferences()
    {
        if (_referencesInjected)
            return;
        TcsLibrary.References =
        [
            Ref("refs/System.Runtime.dll"),
            Ref("refs/System.Collections.dll"),
            Ref("refs/System.Linq.dll"),
            Ref("refs/System.Console.dll"),
            Ref("refs/TinySystem.dll"),
        ];
        _referencesInjected = true;
    }
}

// 補完と hover の stateful session API。single session 固定。
// OpenEditor のたびに projectEpoch を発行し、全 response が epoch を echo する。
// host は現在の epoch と一致しない response を破棄する (sample/言語切替の
// in-flight response が新 project へ届く事故の防止)。
public static partial class SessionExports
{
    // 補完と hover だけの session (OpenEditor。compile しない)
    private static EditorSession? _editor;
    private static int _epoch;

    // 補完と hover だけの session を開く。変換は別の compiler (tcs の wasm) が行い、ここは Roslyn の問い合わせだけに答える
    [JSExport]
    public static string OpenEditor(string requestJson)
    {
        try
        {
            CompilerExports.EnsureReferences();
            var req = JsonSerializer.Deserialize(
                          requestJson, CompileJsonContext.Default.CompileRequest)
                      ?? throw new InvalidOperationException("empty request");
            _epoch++;
            _editor = new EditorSession([.. req.Files.Select(kv => (kv.Key, kv.Value))],
                req.Refs?.Values.ToArray());
            return JsonSerializer.Serialize(new SessionOpenResponse
            {
                Ok = true,
                Epoch = _epoch,
            }, CompileJsonContext.Default.SessionOpenResponse);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new SessionOpenResponse
            {
                Ok = false,
                Errors = [ex.ToString()],
                Epoch = _epoch,
            }, CompileJsonContext.Default.SessionOpenResponse);
        }
    }

    // OpenEditor の session のファイルの今のテキスト。失敗は理由を返す (成功は "")
    [JSExport]
    public static string UpdateText(int epoch, string path, string content)
    {
        if (_editor == null || epoch != _epoch) return $"stale epoch {epoch} (current {_epoch})";
        return _editor.SetText(path, content) ? "" : $"unknown module: {path}";
    }

    // C# を整形する。構文エラーがあるか失敗したら、content をそのまま返す
    [JSExport]
    public static string Format(string content, int width)
    {
        try
        {
            var result = CSharpier.Core.CSharp.CSharpFormatter.Format(content, new CSharpier.Core.CodeFormatterOptions { Width = width });
            return result.CompilationErrors.Any() ? content : result.Code;
        }
        catch (Exception)
        {
            return content;
        }
    }

    // 補完/hover (T230)。speculative fork への読み取り専用クエリで、session
    // の revision/artifacts は変えない。エディタの現在バッファを content で
    // 受け取るため、Update 前の未確定内容でも正しい位置で引ける。
    [JSExport]
    public static string Complete(int epoch, string path, string content,
        int offset)
    {
        try
        {
            var fork = ForkOrThrow(epoch, path, content);
            return JsonSerializer.Serialize(new CompleteResponse
            {
                Ok = true,
                Epoch = _epoch,
                Items = SemanticQueries.Complete(
                    fork.Compilation, fork.Tree, offset),
            }, CompileJsonContext.Default.CompleteResponse);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new CompleteResponse
            {
                Ok = false,
                Epoch = _epoch,
                Error = ex.Message,
            }, CompileJsonContext.Default.CompleteResponse);
        }
    }

    [JSExport]
    public static string Hover(int epoch, string path, string content,
        int offset)
    {
        try
        {
            var fork = ForkOrThrow(epoch, path, content);
            var info = SemanticQueries.Hover(fork.Compilation, fork.Tree, offset);
            return JsonSerializer.Serialize(new HoverResponse
            {
                Ok = true,
                Epoch = _epoch,
                Found = info != null,
                Display = info?.Display,
                Doc = info?.Doc,
                Start = info?.Start ?? 0,
                End = info?.End ?? 0,
            }, CompileJsonContext.Default.HoverResponse);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new HoverResponse
            {
                Ok = false,
                Epoch = _epoch,
                Error = ex.Message,
            }, CompileJsonContext.Default.HoverResponse);
        }
    }

    private static (Microsoft.CodeAnalysis.CSharp.CSharpCompilation Compilation,
        Microsoft.CodeAnalysis.SyntaxTree Tree) ForkOrThrow(
        int epoch, string path, string content)
    {
        if (_editor == null)
            throw new InvalidOperationException("SessionExports.OpenEditor first");
        if (epoch != _epoch)
            throw new InvalidOperationException(
                $"stale epoch {epoch} (current {_epoch})");
        var fork = _editor.ForkWithContent(path, content);
        return fork ?? throw new ArgumentException($"unknown module: {path}");
    }
}
