// Entry that exposes the tcs .NET tools to JS on browser-wasm.
// The JS side calls dotnet.create() → getAssemblyExports("WasmCompiler") → SessionExports (completion, hover, formatting) and
// VmExports (the incremental tcs VM session).
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using TinyCs;

// Main waits only to keep the runtime resident (the actual work is on the JSExport side).
await Task.Delay(Timeout.Infinite);

public sealed class CompileRequest
{
    /// <summary>Input (path -> source).</summary>
    public Dictionary<string, string> Files { get; set; } = [];

    /// <summary>Reference sources for type checking only (equivalent to --ref).</summary>
    public Dictionary<string, string>? Refs { get; set; }
}

/// <summary>Response of SessionExports.OpenEditor.</summary>
public sealed class SessionOpenResponse
{
    public bool Ok { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public int Epoch { get; set; }
    public int Revision { get; set; }
}

/// <summary>Response of SessionExports.Complete (T230).</summary>
public sealed class CompleteResponse
{
    public bool Ok { get; set; }
    public int Epoch { get; set; }
    public List<CompletionItem> Items { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>Response of SessionExports.Hover (T230).</summary>
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

// Reflection-based JsonSerializer is disabled on wasm, so the source generator is used
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

    // Reference assemblies read by the Roslyn compilation (completion and hover). The host has an empty Assembly.Location,
    // so the embedded reference assemblies are injected as byte images.
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

// Stateful session API for completion and hover. Fixed to a single session.
// Each OpenEditor issues a projectEpoch, and every response echoes the epoch.
// The host discards responses that do not match the current epoch (this prevents in-flight responses
// from a sample/language switch from reaching the new project).
public static partial class SessionExports
{
    // Session for completion and hover only (OpenEditor; does not compile)
    private static EditorSession? _editor;
    private static int _epoch;

    // Opens a session for completion and hover only. Conversion is done by a separate compiler (the tcs wasm); this only answers Roslyn queries
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

    // Current text of a file in the OpenEditor session. On failure returns the reason ("" on success)
    [JSExport]
    public static string UpdateText(int epoch, string path, string content)
    {
        if (_editor == null || epoch != _epoch) return $"stale epoch {epoch} (current {_epoch})";
        return _editor.SetText(path, content) ? "" : $"unknown module: {path}";
    }

    // Formats C#. If there is a syntax error or formatting fails, returns content unchanged
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

    // Completion/hover (T230). Read-only queries on a speculative fork that do not change the session's
    // revision/artifacts. The editor's current buffer is received as content,
    // so lookups work at the right position even for uncommitted content before Update.
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
