namespace TinyCs.Vm;

/// <summary>VM backend error (what was recorded in CFail).</summary>
internal sealed class VmException(string message) : Exception(message);

/// <summary>
/// Entry point for driving the VM backend (TcsVm.VmEmitter in SelfHost/VmBackend) from .NET with strings (for tests).
/// The IL is passed as is from the self-hosted compiler's export. The error record (TcsC.CFail) is static, so concurrent calls run one at a time.
/// </summary>
internal static class SelfHostVm
{
    public static readonly object Gate = new();

    /// <summary>The IL that the VM receives. TinyC# diagnostics become InvalidOperationException.</summary>
    private static TcsSelf.ExportResult Export(string[] sources, string[]? refs, string[]? paths)
    {
        var diags = new List<string>();
        var exported = TcsSelf.VmSession.Export([.. sources], paths is null ? [] : [.. paths], refs is null ? [] : [.. refs], diags);
        if (exported is null) throw new InvalidOperationException("TinyC# diagnostics:\n" + string.Join("\n", diags));
        return exported;
    }

    public static byte[] Bytes(List<int> bytes) => [.. bytes.Select(b => (byte)b)];

    /// <summary>Creates a .tcsb. TinyC# diagnostics become InvalidOperationException, and VM errors become VmException.</summary>
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

    /// <summary>reload: the new version's module and plan (same as `tcs vm --reload-from`).</summary>
    public static (byte[] Module, byte[] Plan) EmitReload(string[] v1, string[] v2, string? entryClass,
        IEnumerable<string>? exports = null)
    {
        var old = Export(v1, null, null);
        var next = Export(v2, null, null);
        lock (Gate)
        {
            TcsC.CFail.Error = "";
            var module = TcsVm.VmEmitter.EmitReload(old, next, entryClass ?? "", exports?.ToList() ?? [],
                TcsSelf.VmSession.RestartReasons(old, next), TcsSelf.VmSession.ReinitStatics(old, next), out var plan, out _);
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
