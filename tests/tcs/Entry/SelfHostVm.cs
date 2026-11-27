namespace TinyCs.Vm;

/// <summary>VM backend error (what was recorded in CFail).</summary>
internal sealed class VmException(string message) : Exception(message);

/// <summary>
/// Entry point for driving the VM backend from .NET with strings (for tests): Tcs.TcsToVm.
/// The error record (TcsC.CFail) is static, so concurrent calls run one at a time.
/// </summary>
internal static class SelfHostVm
{
    public static readonly object Gate = new();

    public static byte[] Bytes(List<int> bytes) => [.. bytes.Select(b => (byte)b)];

    /// <summary>Creates a .tcsb. TinyC# diagnostics become InvalidOperationException, and VM errors become VmException.</summary>
    public static byte[] Emit(string[] sources, string? entryClass, IEnumerable<string>? exports = null,
        string[]? refs = null, string[]? paths = null)
    {
        var job = new Tcs.TcsToVm { Entry = entryClass ?? "", Exports = exports?.ToList() ?? [] };
        for (var i = 0; i < sources.Length; i++)
            job.Inputs.Source(paths != null && i < paths.Length ? paths[i] : "", sources[i]);
        foreach (var r in refs ?? [])
            job.Inputs.Reference(r);
        return Bytes(Run(job).Module);
    }

    /// <summary>reload: the new version's module and plan (same as `tcs vm --reload-from`).</summary>
    public static (byte[] Module, byte[] Plan) EmitReload(string[] v1, string[] v2, string? entryClass,
        IEnumerable<string>? exports = null)
    {
        var job = new Tcs.TcsToVm { Entry = entryClass ?? "", Exports = exports?.ToList() ?? [] };
        foreach (var source in v2)
            job.Inputs.Source("", source);
        foreach (var source in v1)
            job.OldSources.Source("", source);
        var result = Run(job);
        return (Bytes(result.Module), Bytes(result.Plan));
    }

    private static Tcs.TcsResult Run(Tcs.TcsToVm job)
    {
        Tcs.TcsResult? result = null;
        lock (Gate)
        {
            while (result == null) result = job.Step();
        }
        if (result.Failure != "") throw new VmException(result.Failure);
        if (result.Failed) throw new InvalidOperationException("TinyC# diagnostics:\n" + string.Join("\n", result.ErrorLines()));
        return result;
    }
}
