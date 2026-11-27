using System.Collections.Generic;

namespace Tcs;

// What a job or a session returns.
public sealed class TcsResult
{
    // "ok" or "error". A session returns "module" (the first build), "batch" (a diff to apply), "restart" (reload the module) or "error" (the last build stays)
    public string Kind = "ok";
    // Errors and warnings of the input, in report order
    public List<Diagnostic> Diagnostics = new List<Diagnostic>();
    // The diagnostics that stopped the job (when Kind is "error")
    public List<Diagnostic> Blocking = new List<Diagnostic>();
    // A failure that is not a diagnostic of the input (a backend error, a missing shipped file). "" if none
    public string Failure = "";
    // Blocking is about the old version given for a reload
    public bool OldVersion;
    // Why the edit cannot be applied live (when Kind is "restart")
    public List<string> Reasons = new List<string>();
    // Changes that were applied live but should be reported (when Kind is "batch")
    public List<string> Notes = new List<string>();
    // Generated C, or IL text
    public string Text = "";
    // .tcsb
    public List<int> Module = new List<int>();
    // Reload plan, and the batch made of the module and the plan
    public List<int> Plan = new List<int>();
    public List<int> Batch = new List<int>();
    // The IL (TcsCompile.Il)
    public ExportResult? Il;

    public bool Failed => Kind == "error";

    // The reason for the failure as lines of text: Failure, or the blocking diagnostics
    public List<string> ErrorLines()
    {
        var lines = new List<string>();
        if (Failure != "") lines.Add(Failure);
        else
        {
            foreach (var d in Blocking) lines.Add(d.ToString());
        }
        return lines;
    }

    public static TcsResult Fail(string failure)
    {
        var r = new TcsResult();
        r.Kind = "error";
        r.Failure = failure;
        return r;
    }
}
