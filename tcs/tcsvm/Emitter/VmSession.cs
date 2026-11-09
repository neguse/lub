namespace TinyCs.Vm;

/// <summary>VM の増分 session (doc/vm-design.md §13.3、V5) の .NET の入口。本体は TinyC# の TcsSelf.VmSession
/// (SelfHost/Tool/VmRun.cs、`tcs vm --watch` と同じもの)。web の playground の worker (WasmCompiler の JSExport) とテストが使う。
/// 診断のある編集は最後の build を変えない。</summary>
public sealed class VmSession(string? entryClass, IReadOnlyList<string> exports, IReadOnlyList<string> referenceSources)
{
    private readonly TcsSelf.VmSession _session = new(entryClass ?? "", [.. exports], [.. referenceSources]);

    /// <summary>Kind は "module" (最初の build。Module を読み込む)、"batch" (Batch を tvm_apply_batch で当てる)、
    /// "restart" (Module を読み直す。Reasons が理由)、"error" (Errors。最後の build のまま)。
    /// Notes は "batch" のとき、live に当てたが知らせたい変化 (instance の field の初期化子の変更など)。</summary>
    public sealed record Result(string Kind, byte[]? Module = null, byte[]? Batch = null,
        IReadOnlyList<string>? Reasons = null, IReadOnlyList<string>? Errors = null, IReadOnlyList<string>? Notes = null);

    public Result Update(IReadOnlyList<string> sources, IReadOnlyList<string>? paths = null)
    {
        TcsSelf.VmUpdate r;
        // 誤りの記録 (TcsC.CFail) は static なので、並んだ呼び出しは 1 つずつ動かす
        lock (SelfHostVm.Gate)
            r = _session.Update([.. sources], paths is null ? [] : [.. paths]);
        return r.Kind switch
        {
            "module" => new Result("module", Module: SelfHostVm.Bytes(r.Module)),
            "restart" => new Result("restart", Module: SelfHostVm.Bytes(r.Module), Reasons: r.Reasons),
            "batch" => new Result("batch", Module: SelfHostVm.Bytes(r.Module), Batch: SelfHostVm.Bytes(r.Batch), Notes: r.Notes),
            _ => new Result("error", Errors: r.Errors),
        };
    }
}
