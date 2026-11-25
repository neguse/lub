using System.Collections.Generic;

namespace TcsSelf;

public sealed class CompileResult
{
    public bool Ok;
    public List<string> Diagnostics = new List<string>();
    public int ErrorCount;
    // Input to tcs c and tcs vm (when Session.ExportOnly)
    public ExportResult? Export;
}
