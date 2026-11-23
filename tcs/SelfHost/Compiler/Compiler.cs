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

// Entry point of the self-hosted compiler
public static class Compiler
{
    // Runs parsing only and returns the diagnostics string (used for speed measurement and syntax checks)
    public static List<string> ParseOnly(List<string> paths, List<string> texts)
    {
        var diags = new Diagnostics();
        for (int i = 0; i < paths.Count; i++)
        {
            var file = new SourceFile(i, paths[i], texts[i], false);
            Parser.ParseFile(file, diags);
        }
        var result = new List<string>();
        foreach (var d in diags.Items) result.Add(d.ToString());
        return result;
    }
}
