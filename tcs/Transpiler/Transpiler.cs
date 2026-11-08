namespace TinyCs;

public class TranspileResult
{
    public string Lua { get; init; } = "";
    public SourceMap? SourceMap { get; init; }
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool Success => Errors.Count == 0;
}

public static class Transpiler
{
    /// <summary>C# を Lua に変換する。compiler はセルフホストの実装 (SelfHost/Compiler)。</summary>
    /// <param name="instanceRegistry">hot reload 用 weak instance registry (`__tcs_instances`) を出す</param>
    /// <param name="tracePrefixes">`--trace`: filePaths がこの prefix に path の
    /// segment 単位で一致するファイルの文に実行の記録を出す</param>
    public static TranspileResult TranspileWithDiagnostics(string[] csharpSources,
        string[]? filePaths = null, string[]? referenceSources = null,
        string? entryClass = null, bool module = false,
        bool instanceRegistry = false, bool cacheTypeLocals = true,
        IReadOnlyList<string>? tracePrefixes = null) =>
        TranspileSelfHosted(csharpSources, filePaths, referenceSources, entryClass, module,
            instanceRegistry, cacheTypeLocals, tracePrefixes);

    public static string Transpile(string csharpSource) => Transpile([csharpSource]);

    public static string Transpile(string[] csharpSources,
        bool instanceRegistry = false, bool cacheTypeLocals = true)
    {
        var result = TranspileWithDiagnostics(csharpSources,
            instanceRegistry: instanceRegistry, cacheTypeLocals: cacheTypeLocals);
        if (!result.Success)
            throw new InvalidOperationException(
                string.Join("\n", result.Errors));
        return result.Lua;
    }

    private static TranspileResult TranspileSelfHosted(string[] csharpSources, string[]? filePaths,
        string[]? referenceSources, string? entryClass, bool module, bool instanceRegistry, bool cacheTypeLocals,
        IReadOnlyList<string>? tracePrefixes)
    {
        var session = new TcsSelf.Session(entryClass ?? "")
        {
            InstanceRegistry = instanceRegistry,
            CacheTypeLocals = cacheTypeLocals,
            Module = module,
            TracePrefixes = [.. tracePrefixes ?? []],
        };
        for (var i = 0; i < csharpSources.Length; i++)
            session.Add(filePaths != null && i < filePaths.Length ? filePaths[i] : "", csharpSources[i], 0);
        foreach (var source in referenceSources ?? [])
            session.Add("", source, 1);
        foreach (var (path, text) in TcsLibrary.LibrarySourcesFor())
            session.Add(path, text, 2);
        var result = session.Compile();
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var d in result.Diagnostics)
        {
            if (d.Contains(": warning ") || d.StartsWith("warning ", StringComparison.Ordinal)) warnings.Add(d);
            else errors.Add(d);
        }
        if (!result.Ok || errors.Count > 0)
            return new TranspileResult { Errors = errors.Count > 0 ? errors : ["compile failed"], Warnings = warnings };
        var map = new SourceMap();
        foreach (var e in result.SourceMap) map.Add(e.LuaLine, e.File, e.Line);
        return new TranspileResult { Lua = result.Lua, SourceMap = map, Warnings = warnings };
    }
}
