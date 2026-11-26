namespace TinyCs;

/// <summary>Result of the same checks as `tcs check`. Does not return IL.</summary>
public sealed class CheckResult
{
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool Success => Errors.Count == 0;
}

/// <summary>.NET entry point (for tests) to the same checks as `tcs check` (Tcs.Session.CheckOnly).
/// After the binding and compliance checks, bodies that cannot be built into IL, entry class errors, and internal build errors are also errors.</summary>
public static class Checker
{
    public static CheckResult Check(string[] csharpSources, string[]? filePaths = null,
        string[]? referenceSources = null, string? entryClass = null)
    {
        var session = new Tcs.Session(entryClass ?? "") { CheckOnly = true };
        for (var i = 0; i < csharpSources.Length; i++)
            session.Add(filePaths != null && i < filePaths.Length ? filePaths[i] : "", csharpSources[i], 0);
        foreach (var source in referenceSources ?? [])
            session.Add("", source, 1);
        session.Add("TinySystem/Linq.cs", TcsHost.ToolFile("lib/TinySystem/Linq.cs"), 2);
        session.Add("TinySystem/ValueTuple.cs", TcsHost.ToolFile("lib/TinySystem/ValueTuple.cs"), 2);
        var result = session.Compile();
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var d in result.Diagnostics)
        {
            if (d.Contains(": warning ") || d.StartsWith("warning ", StringComparison.Ordinal)) warnings.Add(d);
            else errors.Add(d);
        }
        if (!result.Ok && errors.Count == 0) errors.Add("compile failed");
        return new CheckResult { Errors = errors, Warnings = warnings };
    }
}
