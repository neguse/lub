namespace TinyCs;

// IL→C backend (tcs c) と VM backend の入力 (doc/il-spec.md、契約の正本は doc/il-reference.md)。
// IL の型はセルフホストの compiler のもの (SelfHost/Compiler/Il.cs と IlExport.cs の TcsSelf.ExportResult) をそのまま使う。
// ここは .NET の呼び出し側 (テスト) のための入口で、誤りの扱いを決める。
public static class IlExport
{
    // tcs VM が許す準拠の誤り (--ref の型の検査と interface の実装)。これだけなら IL を作って診断を付ける (VmEmitter.Diagnostics と同じ)
    private static readonly string[] VmAllowed =
        ["RefInterfaceImplementation(", "RefTypeTest(", "RefCast(", "RefInterfaceTypeTest("];

    /// <summary>誤りのある入力は IL にせず (Classes は空)、Diagnostics に誤りを入れて返す。例外は tcs VM が許す準拠の誤りだけで、
    /// そのときは IL と一緒に診断として返す (tcs c は診断で止め、VM は許す)。</summary>
    public static TcsSelf.ExportResult Export(string[] csharpSources, bool specializeGenerics = false,
        string[]? referenceSources = null, string[]? sourcePaths = null)
    {
        var session = new TcsSelf.Session("") { ExportOnly = true, Specialize = specializeGenerics };
        for (var i = 0; i < csharpSources.Length; i++)
            session.Add(sourcePaths != null && i < sourcePaths.Length ? sourcePaths[i] : "", csharpSources[i], 0);
        foreach (var source in referenceSources ?? [])
            session.Add("", source, 1);
        foreach (var (path, text) in TcsLibrary.LibrarySourcesFor())
            session.Add(path, text, 2);
        var result = session.Compile();
        var errors = result.Diagnostics.Where(d => !d.Contains(": warning ") && !d.StartsWith("warning ", StringComparison.Ordinal)).ToList();
        if (result.Export == null || errors.Any(e => !VmAllowed.Any(p => e.Contains("unsupported syntax: " + p, StringComparison.Ordinal))))
        {
            var failed = new TcsSelf.ExportResult();
            failed.Diagnostics.AddRange(errors.Count > 0 ? errors : ["export failed"]);
            return failed;
        }
        result.Export.Diagnostics.InsertRange(0, errors);
        return result.Export;
    }
}
