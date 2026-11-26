namespace TinyCs.Tcs2c;

/// <summary>C backend error (what was recorded in CFail).</summary>
internal sealed class Tcs2cException(string message) : Exception(message);

/// <summary>
/// Entry point for driving the C backend (TcsC.CEmitter in CBackend) from .NET with strings (for tests).
/// The IL is passed as is from the self-hosted compiler's export (Tcs.ExportResult), and the runtime C is read from runtime/ in the output dir.
/// The error record (TcsC.CFail) is static, so concurrent calls run one at a time.
/// </summary>
internal static class SelfHostC
{
    private static readonly object Gate = new();
    private static readonly Lazy<Dictionary<string, string>> Runtime = new(LoadRuntime);

    /// <summary>Generates C. TinyC# diagnostics become InvalidOperationException, and C backend errors become Tcs2cException.</summary>
    public static string Emit(string[] sources, string? entryClass, bool lib = false, bool digestF32 = false,
        string[]? referenceSources = null)
    {
        var session = new Tcs.Session("") { ExportOnly = true, Specialize = true };
        foreach (var source in sources)
            session.Add("", source, 0);
        foreach (var source in referenceSources ?? [])
            session.Add("", source, 1);
        session.Add("TinySystem/Linq.cs", TcsHost.ToolFile("lib/TinySystem/Linq.cs"), 2);
        session.Add("TinySystem/ValueTuple.cs", TcsHost.ToolFile("lib/TinySystem/ValueTuple.cs"), 2);
        var result = session.Compile();
        var errors = result.Diagnostics
            .Where(d => !d.Contains(": warning ") && !d.StartsWith("warning ", StringComparison.Ordinal)).ToList();
        if (result.Export == null || errors.Count > 0)
            throw new InvalidOperationException("TinyC# diagnostics:\n" + string.Join("\n", errors.Count > 0 ? errors : ["export failed"]));
        // Bodies and expressions that could not become IL (stops, like CRun of `tcs c`)
        if (result.Export.InternalErrors.Count > 0)
            throw new InvalidOperationException("TinyC# diagnostics:\n" + string.Join("\n", result.Export.InternalErrors.Select(e => "internal: " + e)));
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
