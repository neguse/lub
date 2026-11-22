// A named incremental tcs session (tcs/SelfHost/Tool/Sessions.cs). Opened with the tcs built into the lider player (Tcs.ref.cs).
// Different versions of the same program (such as the saved and edited versions) can be compiled side by side under different names.
// When a compile succeeds, writes a module (.tcsb) or a batch (diff) to the path given at open. The game app (App.Use) reads it.
// Results of requested compiles are received with Take.
// Currently the compile runs right when requested (that frame stalls for the duration of the compile), so a Take after the request always receives the result.
using System.Collections.Generic;

namespace LiderCore;

// Result of one compile
public class TcsResult
{
    // module (first build), batch (apply a diff), restart (reload the module), error (stays at the last successful version)
    public string Kind = "";
    // Number of successful compiles
    public int Revision;
    // Diagnostic (when Kind is error)
    public readonly List<string> Errors = new List<string>();
    // Reason for recreating (when Kind is restart)
    public readonly List<string> Reasons = new List<string>();
    // Changes that were applied but should be reported (when Kind is batch)
    public readonly List<string> Notes = new List<string>();

    public static TcsResult Parse(string reply)
    {
        var r = new TcsResult();
        var first = true;
        foreach (var line in reply.Split('\n'))
        {
            if (line == "")
                continue;
            if (first)
            {
                r.Kind = line;
                first = false;
            }
            else if (line.StartsWith("revision ") && int.TryParse(Utf8.Sub(line, 9), out var n))
                r.Revision = n;
            else if (line.StartsWith("error "))
                r.Errors.Add(Utf8.Sub(line, 6));
            else if (line.StartsWith("reason "))
                r.Reasons.Add(Utf8.Sub(line, 7));
            else if (line.StartsWith("note "))
                r.Notes.Add(Utf8.Sub(line, 5));
        }
        return r;
    }
}

public class TcsSession
{
    public readonly string Name;
    readonly List<TcsResult> done = new List<TcsResult>();

    public TcsSession(string name)
    {
        Name = name;
    }

    // Opens the session (reopens it if the name is the same) and compiles files.
    // entry is the entry class, modulePath and batchPath are the paths the results are written to, exports are the functions to generate besides the entry (C.M or C.*), traces are the --trace path prefixes, and refs are the texts of references (declarations that are not compiled).
    // paths and texts are the paths and texts of the files to compile (in the same order)
    public void Open(string entry, string modulePath, string batchPath, List<string> exports, List<string> traces, List<string> refs,
        List<string> paths, List<string> texts)
    {
        Tcs.Arg("open");
        Tcs.Arg(Name);
        Tcs.Arg(entry);
        Tcs.Arg(modulePath);
        Tcs.Arg(batchPath);
        ArgList(exports);
        ArgList(traces);
        ArgList(refs);
        for (var i = 0; i < paths.Count && i < texts.Count; i++)
        {
            Tcs.Arg(paths[i]);
            Tcs.Arg(texts[i]);
        }
        Call();
    }

    // Removes the files in removed, adds the files in paths or replaces their text with texts, and compiles everything at once
    public void Update(List<string> removed, List<string> paths, List<string> texts)
    {
        Tcs.Arg("update");
        Tcs.Arg(Name);
        ArgList(removed);
        for (var i = 0; i < paths.Count && i < texts.Count; i++)
        {
            Tcs.Arg(paths[i]);
            Tcs.Arg(texts[i]);
        }
        Call();
    }

    // Adds one file or replaces its text, and compiles
    public void Update(string path, string text)
    {
        Update(new List<string>(), new List<string> { path }, new List<string> { text });
    }

    public void Close()
    {
        Tcs.Arg("close");
        Tcs.Arg(Name);
        Tcs.Call(out _);
        done.Clear();
    }

    // Receives the results of finished compiles in request order (received results are removed)
    public List<TcsResult> Take()
    {
        var taken = new List<TcsResult>(done);
        done.Clear();
        return taken;
    }

    static void ArgList(List<string> items)
    {
        Tcs.Arg("" + items.Count);
        foreach (var item in items)
            Tcs.Arg(item);
    }

    void Call()
    {
        if (Tcs.Call(out var reply) && reply != null)
            done.Add(TcsResult.Parse(reply));
        else
        {
            var failed = new TcsResult { Kind = "error" };
            failed.Errors.Add("the tcs compiler is not in this player");
            done.Add(failed);
        }
    }
}
