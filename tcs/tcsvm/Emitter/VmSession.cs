namespace TinyCs.Vm;

/// <summary>.NET entry point for the VM incremental session (docs/tcs/vm-design.md §13.3, V5). The core is TinyC#'s TcsSelf.VmSession
/// (Tool/VmRun.cs, the same as `tcs vm --watch`). Used by tests.
/// Edits with diagnostics do not change the last build.</summary>
public sealed class VmSession(string? entryClass, IReadOnlyList<string> exports, IReadOnlyList<string> referenceSources)
{
    private readonly TcsSelf.VmSession _session = new(entryClass ?? "", [.. exports], [.. referenceSources]);

    /// <summary>Kind is "module" (the first build; loads Module), "batch" (applies Batch with tvm_apply_batch),
    /// "restart" (reloads Module; Reasons gives the reasons), or "error" (Errors; the last build stays).
    /// Notes, for "batch", are changes that were applied live but should still be reported (such as changes to instance field initializers).</summary>
    public sealed record Result(string Kind, byte[]? Module = null, byte[]? Batch = null,
        IReadOnlyList<string>? Reasons = null, IReadOnlyList<string>? Errors = null, IReadOnlyList<string>? Notes = null);

    public Result Update(IReadOnlyList<string> sources, IReadOnlyList<string>? paths = null)
    {
        TcsSelf.VmUpdate r;
        // The error record (TcsC.CFail) is static, so concurrent calls run one at a time
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
