namespace TinyCs;

/// <summary>Result of the same checks as `tcs check`. Does not return IL.</summary>
public sealed class CheckResult
{
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool Success => Errors.Count == 0;
}

/// <summary>.NET entry point (for tests) to the same checks as `tcs check` (Tcs.TcsCompile.Check).
/// After the binding and compliance checks, bodies that cannot be built into IL, entry class errors, and internal build errors are also errors.</summary>
public static class Checker
{
    public static CheckResult Check(string[] csharpSources, string[]? filePaths = null,
        string[]? referenceSources = null, string? entryClass = null)
    {
        var job = Tcs.TcsCompile.Check();
        job.Entry = entryClass ?? "";
        for (var i = 0; i < csharpSources.Length; i++)
            job.Inputs.Source(filePaths != null && i < filePaths.Length ? filePaths[i] : "", csharpSources[i]);
        foreach (var source in referenceSources ?? [])
            job.Inputs.Reference(source);
        var result = job.Run();
        return new CheckResult
        {
            Errors = [.. result.Blocking.Select(d => d.ToString())],
            Warnings = [.. result.Diagnostics.Where(d => !d.IsError).Select(d => d.ToString())],
        };
    }
}
