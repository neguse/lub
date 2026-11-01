namespace TinyCs.Vm;

/// <summary>VM の増分 session (doc/vm-design.md §13.3、V5): 最後に通った build を持ち、編集ごとに reload の batch か
/// restart を返す。`tcs2vm --watch` と web の playground の worker (WasmCompiler の JSExport) が同じものを使う。
/// 診断のある編集は最後の build を変えない。</summary>
public sealed class VmSession(string? entryClass, IReadOnlyList<string> exports, IReadOnlyList<string> referenceSources)
{
    private IlExportResult? _last;

    /// <summary>Kind は "module" (最初の build。Module を読み込む)、"batch" (Batch を tvm_apply_batch で当てる)、
    /// "restart" (Module を読み直す。Reasons が理由)、"error" (Errors。最後の build のまま)。</summary>
    public sealed record Result(string Kind, byte[]? Module = null, byte[]? Batch = null,
        IReadOnlyList<string>? Reasons = null, IReadOnlyList<string>? Errors = null);

    public Result Update(IReadOnlyList<string> sources, IReadOnlyList<string>? paths = null)
    {
        try
        {
            var next = IlExport.Export([.. sources], specializeGenerics: true,
                referenceSources: [.. referenceSources], sourcePaths: paths is null ? null : [.. paths]);
            if (VmEmitter.Diagnostics(next) is { Length: > 0 } diags) return new Result("error", Errors: diags);
            if (_last is null)
            {
                var module = VmEmitter.Emit(next, entryClass, exports);
                _last = next;
                return new Result("module", Module: module);
            }
            var (m, plan, reasons) = VmEmitter.EmitReloadWithReasons(_last, next, entryClass, exports);
            _last = next;
            return reasons.Count > 0
                ? new Result("restart", Module: m, Reasons: reasons)
                : new Result("batch", Module: m, Batch: VmEmitter.Batch(m, plan));
        }
        catch (VmException ex)
        {
            return new Result("error", Errors: [ex.Message]);
        }
    }
}
