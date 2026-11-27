using System.Collections.Generic;

namespace Tcs;

public sealed class CompileResult
{
    public bool Ok;
    // Errors and warnings in report order
    public List<Diagnostic> Diagnostics = new List<Diagnostic>();
    public int ErrorCount;
    // Input to tcs c and tcs vm (when Session.ExportOnly)
    public ExportResult? Export;
}
