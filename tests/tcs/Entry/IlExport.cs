namespace TinyCs;

// Input of the IL→C backend (tcs c) and the VM backend (docs/tcs/il-spec.md; the source of truth for the contract is docs/tcs/il-reference.md).
// The IL types are those of the self-host compiler (Tcs.ExportResult in Compiler/Il.cs and IlExport.cs), used as is.
// This is the entry point for .NET callers (tests) to Tcs.TcsCompile.Il.
public static class IlExport
{
    /// <summary>Input with errors is not made into IL (Classes is empty) and is returned with the errors in Diagnostics. The only exception is compliance errors the tcs VM accepts,
    /// which are returned as diagnostics together with the IL (tcs c stops on the diagnostics, the VM accepts them).</summary>
    public static Tcs.ExportResult Export(string[] csharpSources, bool specializeGenerics = false,
        string[]? referenceSources = null, string[]? sourcePaths = null)
    {
        var job = Tcs.TcsCompile.Il(specializeGenerics);
        for (var i = 0; i < csharpSources.Length; i++)
            job.Inputs.Source(sourcePaths != null && i < sourcePaths.Length ? sourcePaths[i] : "", csharpSources[i]);
        foreach (var source in referenceSources ?? [])
            job.Inputs.Reference(source);
        var result = job.Run();
        var errors = result.Diagnostics.Where(d => d.IsError).Select(d => d.ToString());
        if (result.Il == null)
        {
            var failed = new Tcs.ExportResult();
            failed.Diagnostics.AddRange(errors);
            return failed;
        }
        result.Il.Diagnostics.InsertRange(0, errors);
        return result.Il;
    }
}
