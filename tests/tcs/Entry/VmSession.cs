namespace TinyCs.Vm;

/// <summary>.NET entry point for the VM session that continues across edits (docs/tcs/vm-design.md §13.3): Tcs.TcsSession,
/// the same as `tcs vm --watch`. Used by tests.
/// Edits with diagnostics do not change the last build.</summary>
public sealed class VmSession
{
    private readonly Tcs.TcsSession _session;
    private List<string> _names = [];

    public VmSession(string? entryClass, IReadOnlyList<string> exports, IReadOnlyList<string> referenceSources)
    {
        _session = new Tcs.TcsSession { Entry = entryClass ?? "", Exports = [.. exports] };
        foreach (var r in referenceSources)
            _session.Reference(r);
    }

    /// <summary>Kind is "module" (the first build; loads Module), "batch" (applies Batch with tvm_apply_batch),
    /// "restart" (reloads Module; Reasons gives the reasons), or "error" (Errors; the last build stays).
    /// Notes, for "batch", are changes that were applied live but should still be reported (such as changes to instance field initializers).</summary>
    public sealed record Result(string Kind, byte[]? Module = null, byte[]? Batch = null,
        IReadOnlyList<string>? Reasons = null, IReadOnlyList<string>? Errors = null, IReadOnlyList<string>? Notes = null);

    /// <summary>Builds with sources as the whole input (a source without a path is named by its position).</summary>
    public Result Update(IReadOnlyList<string> sources, IReadOnlyList<string>? paths = null)
    {
        var names = sources.Select((_, i) => paths != null && i < paths.Count && paths[i] != "" ? paths[i] : "#" + i).ToList();
        Tcs.TcsResult? r = null;
        // The error record (TcsC.CFail) is static, so concurrent calls run one at a time
        lock (SelfHostVm.Gate)
        {
            foreach (var old in _names.Where(n => !names.Contains(n)))
                _session.Remove(old);
            _names = names;
            for (var i = 0; i < sources.Count; i++)
                _session.Source(names[i], sources[i]);
            _session.Start();
            while (r == null) r = _session.Step();
        }
        return r.Kind switch
        {
            "module" => new Result("module", Module: SelfHostVm.Bytes(r.Module)),
            "restart" => new Result("restart", Module: SelfHostVm.Bytes(r.Module), Reasons: r.Reasons),
            "batch" => new Result("batch", Module: SelfHostVm.Bytes(r.Module), Batch: SelfHostVm.Bytes(r.Batch), Notes: r.Notes),
            _ => new Result("error", Errors: r.ErrorLines()),
        };
    }
}
