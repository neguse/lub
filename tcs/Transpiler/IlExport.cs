namespace TinyCs;

// Input of the IL→C backend (tcs c) and the VM backend (doc/il-spec.md; the source of truth for the contract is doc/il-reference.md).
// The IL types are those of the self-host compiler (TcsSelf.ExportResult in SelfHost/Compiler/Il.cs and IlExport.cs), used as is.
// This is the entry point for .NET callers (tests), and it decides how errors are handled.
public static class IlExport
{
    // Compliance errors the tcs VM allows (--ref type checks and interface implementation). If these are the only errors, IL is built and the diagnostics are attached (same as VmEmitter.Diagnostics)
    private static readonly string[] VmAllowed =
        ["RefInterfaceImplementation(", "RefTypeTest(", "RefCast(", "RefInterfaceTypeTest("];

    /// <summary>Input with errors is not made into IL (Classes is empty) and is returned with the errors in Diagnostics. The only exception is compliance errors the tcs VM allows,
    /// which are returned as diagnostics together with the IL (tcs c stops on the diagnostics, the VM allows them).</summary>
    public static TcsSelf.ExportResult Export(string[] csharpSources, bool specializeGenerics = false,
        string[]? referenceSources = null, string[]? sourcePaths = null)
    {
        var session = new TcsSelf.Session("") { ExportOnly = true, Specialize = specializeGenerics };
        for (var i = 0; i < csharpSources.Length; i++)
            session.Add(sourcePaths != null && i < sourcePaths.Length ? sourcePaths[i] : "", csharpSources[i], 0);
        foreach (var source in referenceSources ?? [])
            session.Add("", source, 1);
        foreach (var (path, text) in TcsLibrary.LibrarySourcesFor())
            session.Add(path, text, 2);
        var result = session.Compile();
        var errors = result.Diagnostics.Where(d => !d.Contains(": warning ") && !d.StartsWith("warning ", StringComparison.Ordinal)).ToList();
        if (result.Export == null || errors.Any(e => !VmAllowed.Any(p => e.Contains("unsupported syntax: " + p, StringComparison.Ordinal))))
        {
            var failed = new TcsSelf.ExportResult();
            failed.Diagnostics.AddRange(errors.Count > 0 ? errors : ["export failed"]);
            return failed;
        }
        result.Export.Diagnostics.InsertRange(0, errors);
        return result.Export;
    }
}
