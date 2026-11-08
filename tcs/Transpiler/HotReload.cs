namespace TinyCs;

// hot reload の reload chunk (il-design §6: eager migration)。
// 規則と chunk の組み立ての本体は SelfHost/Tool/Reload.cs (TinyC#、`tcs --watch --reload-chunks` と同じ実装)。
// ここは .NET の呼び出し側 (テスト) のための入口で、v1 と v2 のソースから計画を作る。
// 前提: v1 は instance registry 付き (`--instance-registry`) で変換して実行済みで、返り値の chunk を同じ VM で 1 つの chunk として実行する
public static class HotReload
{
    // restart の理由があれば chunk は作らない
    public sealed record ReloadPlan(string? Chunk, IReadOnlyList<string> RestartReasons);

    // v2FilePaths / tracePrefixes: v2 の定義の出力に渡す入力の path と `--trace` の prefix (reload chunk も出力と同じ trace を出す)
    public static ReloadPlan PlanReload(string[] v1Sources, string[] v2Sources,
        string[]? v1References = null, string[]? v2References = null,
        string[]? v2FilePaths = null, IReadOnlyList<string>? tracePrefixes = null)
    {
        var errors = new List<string>();
        var oldExport = Export(v1Sources, v1References, errors);
        var newExport = Export(v2Sources, v2References, errors);
        // 型の差分は IL から取るので、IL にできないソースでは移行を組めない
        if (oldExport == null || newExport == null)
            throw new InvalidOperationException(string.Join("\n", errors));
        var reasons = TcsSelf.ReloadRules.RestartReasons(TcsSelf.ReloadRules.TypesOf(oldExport),
            TcsSelf.ReloadRules.TypesOf(newExport), true);
        if (reasons.Count > 0)
            return new ReloadPlan(null, reasons);
        // v2 の chunk は同じ名前の global を旧い identity に戻してから method の本文が解決する前提なので、型の table を local に置かない
        var v2 = Transpiler.TranspileWithDiagnostics(v2Sources, v2FilePaths,
            referenceSources: v2References, instanceRegistry: true,
            cacheTypeLocals: false, tracePrefixes: tracePrefixes);
        if (!v2.Success)
            throw new InvalidOperationException(string.Join("\n", v2.Errors));
        return new ReloadPlan(TcsSelf.ReloadChunk.Emit(oldExport, newExport, v2.Lua,
            tracePrefixes is { Count: > 0 }), []);
    }

    public static string EmitReloadChunk(string[] v1Sources, string[] v2Sources,
        string[]? v1References = null, string[]? v2References = null)
    {
        var plan = PlanReload(v1Sources, v2Sources, v1References, v2References);
        return plan.Chunk ?? throw new InvalidOperationException(
            "restart required: " + string.Join("; ", plan.RestartReasons));
    }

    private static TcsSelf.ExportResult? Export(string[] sources, string[]? references, List<string> errors)
    {
        var session = new TcsSelf.Session("") { ExportOnly = true };
        foreach (var source in sources)
            session.Add("", source, 0);
        foreach (var source in references ?? [])
            session.Add("", source, 1);
        foreach (var (path, text) in TcsLibrary.LibrarySourcesFor())
            session.Add(path, text, 2);
        var result = session.Compile();
        var found = result.Diagnostics.Where(d => !d.Contains(": warning ") && !d.StartsWith("warning ", StringComparison.Ordinal)).ToList();
        if (found.Count > 0 || result.Export == null)
        {
            errors.AddRange(found.Count > 0 ? found : ["export failed"]);
            return null;
        }
        return result.Export;
    }
}
