namespace TinyCs.Vm;

/// <summary>VM backend の誤り (CFail に記録したもの)。</summary>
internal sealed class VmException(string message) : Exception(message);

/// <summary>
/// VM backend (SelfHost/VmBackend の TcsVm.VmEmitter) を .NET から文字列で動かす入口 (テストのため)。
/// IL はセルフホストの compiler の書き出しをそのまま渡す。誤りの記録 (TcsC.CFail) は static なので、並んだ呼び出しは 1 つずつ動かす。
/// </summary>
internal static class SelfHostVm
{
    public static readonly object Gate = new();

    /// <summary>VM が受ける IL。TinyC# の診断は InvalidOperationException。</summary>
    private static TcsSelf.ExportResult Export(string[] sources, string[]? refs, string[]? paths)
    {
        var diags = new List<string>();
        var exported = TcsSelf.VmSession.Export([.. sources], paths is null ? [] : [.. paths], refs is null ? [] : [.. refs], diags);
        if (exported is null) throw new InvalidOperationException("TinyC# diagnostics:\n" + string.Join("\n", diags));
        return exported;
    }

    public static byte[] Bytes(List<int> bytes) => [.. bytes.Select(b => (byte)b)];

    /// <summary>.tcsb を作る。TinyC# の診断は InvalidOperationException、VM の誤りは VmException。</summary>
    public static byte[] Emit(string[] sources, string? entryClass, IEnumerable<string>? exports = null,
        string[]? refs = null, string[]? paths = null)
    {
        var exported = Export(sources, refs, paths);
        lock (Gate)
        {
            TcsC.CFail.Error = "";
            var bytes = TcsVm.VmEmitter.Emit(exported, entryClass ?? "", exports?.ToList() ?? []);
            Throw();
            return Bytes(bytes);
        }
    }

    /// <summary>reload: 新版の module と計画 (`tcs vm --reload-from` と同じ)。</summary>
    public static (byte[] Module, byte[] Plan) EmitReload(string[] v1, string[] v2, string? entryClass,
        IEnumerable<string>? exports = null)
    {
        var old = Export(v1, null, null);
        var next = Export(v2, null, null);
        lock (Gate)
        {
            TcsC.CFail.Error = "";
            var module = TcsVm.VmEmitter.EmitReload(old, next, entryClass ?? "", exports?.ToList() ?? [],
                TcsSelf.VmSession.RestartReasons(old, next), out var plan, out _);
            Throw();
            return (Bytes(module), Bytes(plan));
        }
    }

    private static void Throw()
    {
        if (!TcsC.CFail.Failed) return;
        var message = TcsC.CFail.Error;
        TcsC.CFail.Error = "";
        throw new VmException(message);
    }
}
