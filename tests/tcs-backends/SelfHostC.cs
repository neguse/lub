namespace TinyCs.Tcs2c;

/// <summary>C backend error (what was recorded in CFail).</summary>
internal sealed class Tcs2cException(string message) : Exception(message);

/// <summary>
/// Entry point for driving the C backend (TcsC.CEmitter in CBackend) from .NET with strings (for tests).
/// The error record (TcsC.CFail) is static, so concurrent calls run one at a time.
/// </summary>
internal static class SelfHostC
{
    private static readonly object Gate = new();

    /// <summary>Generates C (Tcs.TcsToC). TinyC# diagnostics become InvalidOperationException, and C backend errors become Tcs2cException.</summary>
    public static string Emit(string[] sources, string? entryClass, bool lib = false, bool digestF32 = false,
        string[]? referenceSources = null)
    {
        var job = new Tcs.TcsToC { Entry = entryClass, Lib = lib, DigestF32 = digestF32 };
        foreach (var source in sources)
            job.Inputs.Source("", source);
        foreach (var source in referenceSources ?? [])
            job.Inputs.Reference(source);
        Tcs.TcsResult? result = null;
        lock (Gate)
        {
            while (result == null) result = job.Step();
        }
        if (result.Failure != "") throw new Tcs2cException(result.Failure);
        if (result.Failed) throw new InvalidOperationException("TinyC# diagnostics:\n" + string.Join("\n", result.ErrorLines()));
        return result.Text;
    }
}
