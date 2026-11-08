namespace TinyCs.Tcs2c;

/// <summary>C backend の誤り (CFail に記録したもの)。</summary>
internal sealed class Tcs2cException(string message) : Exception(message);

/// <summary>
/// C backend (SelfHost/CBackend の TcsC.CEmitter) を .NET から文字列で動かす入口 (テストのため)。
/// IL はセルフホストの compiler の書き出し (TcsSelf.ExportResult) をそのまま渡し、runtime の C は出力の dir の runtime/ から読む。
/// 誤りの記録 (TcsC.CFail) は static なので、並んだ呼び出しは 1 つずつ動かす。
/// </summary>
internal static class SelfHostC
{
    private static readonly object Gate = new();
    private static readonly Lazy<Dictionary<string, string>> Runtime = new(LoadRuntime);

    /// <summary>C を生成する。TinyC# の診断は InvalidOperationException、C backend の誤りは Tcs2cException。</summary>
    public static string Emit(string[] sources, string? entryClass, bool lib = false, bool digestF32 = false,
        string[]? referenceSources = null)
    {
        var session = new TcsSelf.Session("") { ExportOnly = true, Specialize = true };
        foreach (var source in sources)
            session.Add("", source, 0);
        foreach (var source in referenceSources ?? [])
            session.Add("", source, 1);
        foreach (var (path, text) in TcsLibrary.LibrarySourcesFor())
            session.Add(path, text, 2);
        var result = session.Compile();
        var errors = result.Diagnostics
            .Where(d => !d.Contains(": warning ") && !d.StartsWith("warning ", StringComparison.Ordinal)).ToList();
        if (result.Export == null || errors.Count > 0)
            throw new InvalidOperationException("TinyC# diagnostics:\n" + string.Join("\n", errors.Count > 0 ? errors : ["export failed"]));
        lock (Gate)
        {
            TcsC.CFail.Error = "";
            var c = new TcsC.CEmitter(result.Export, digestF32, Runtime.Value).Emit(entryClass, lib);
            if (TcsC.CFail.Failed)
            {
                var message = TcsC.CFail.Error;
                TcsC.CFail.Error = "";
                throw new Tcs2cException(message);
            }
            return c;
        }
    }

    private static Dictionary<string, string> LoadRuntime()
    {
        var files = new Dictionary<string, string>();
        foreach (var name in TcsC.CEmitter.RuntimeFileNames())
            files[name] = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "runtime", name));
        return files;
    }
}
