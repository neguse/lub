namespace TinyCs;

/// <summary>`tcs check` と同じ検査の結果。IL を返さない。</summary>
public sealed class CheckResult
{
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool Success => Errors.Count == 0;
}

/// <summary>`tcs check` と同じ検査 (TcsSelf.Session.CheckOnly) の .NET の入口 (テストのため)。
/// 束縛と準拠の検査の後、IL を組み立てられない本文、entry class の誤り、組み立ての内部の誤りも誤りにする。</summary>
public static class Checker
{
    public static CheckResult Check(string[] csharpSources, string[]? filePaths = null,
        string[]? referenceSources = null, string? entryClass = null)
    {
        var session = new TcsSelf.Session(entryClass ?? "") { CheckOnly = true };
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
        if (!result.Ok && errors.Count == 0) errors.Add("compile failed");
        return new CheckResult { Errors = errors, Warnings = warnings };
    }
}
