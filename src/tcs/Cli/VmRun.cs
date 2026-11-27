using System.Collections.Generic;
using TcsC;
using TcsVm;

namespace Tcs;

// `tcs vm`: builds a tcs VM .tcsb. VmBackend turns the IL into a .tcsb.
public sealed class VmOptions
{
    public const string Usage = "usage: tcs vm [--entry CLASS] [--export CLASS.METHOD|CLASS.*] [--ref HOST_STUB.cs] [--watch [--inputs-from LIST]] [--trace PATH_PREFIX] [--dump-il] [--reload-from OLD.cs ... --plan OUT.plan | --batch OUT.batch] [-o OUT.tcsb] INPUT.cs ...";
    public List<string> Inputs = new List<string>();
    // File listing the --watch inputs (one path per line). Reloaded each time it changes.
    public string InputsFrom = "";
    // --trace: records statements and assignments in files under this path (the tcsvm.h check)
    public List<string> Traces = new List<string>();
    public string Output = "";
    public string Entry = "";
    public bool Dump;
    public List<string> Exports = new List<string>();
    public List<string> ReloadFrom = new List<string>();
    public List<string> Refs = new List<string>();
    public bool Watch;
    public string PlanOut = "";
    public string BatchOut = "";
}

public sealed class VmRun
{
    private VmOptions _o;
    private TcsSession? _watchSession;
    // Names of the sources in the watch session (to remove the ones that left the input list)
    private List<string> _watched = new List<string>();
    private string _stamp = "";
    // Whether the first build under --watch passed
    private bool _built;

    public VmRun(VmOptions o)
    {
        _o = o;
    }

    // The first of paths that is not a file ("" if all are)
    private string MissingFile(List<string> paths)
    {
        foreach (var p in paths)
        {
            if (TcsFiles.Stamp(p) == "") return p;
        }
        return "";
    }

    public static int VError(string message)
    {
        CliProcess.Err("tcs vm: " + message + "\n");
        return 1;
    }

    // If the output path is the same file as any input (inputs, --ref, --reload-from, --inputs-from), the error message
    private string InputClash(string output)
    {
        var inputs = new List<string>();
        foreach (var p in _o.Refs) inputs.Add(p);
        foreach (var p in _o.ReloadFrom) inputs.Add(p);
        if (_o.InputsFrom != "") inputs.Add(_o.InputsFrom);
        return Cli.InputClash(output, _o.Inputs, inputs);
    }

    // Before writing, checks that no input gets overwritten (stops without writing anything)
    private string OutputsClash()
    {
        string clash = InputClash(_o.Output);
        if (clash == "") clash = InputClash(_o.PlanOut);
        if (clash == "") clash = InputClash(_o.BatchOut);
        return clash;
    }

    private int WriteBytes(string path, List<int> bytes)
    {
        string clash = InputClash(path);
        if (clash != "") return VError(clash);
        string failure = TcsFiles.WriteBytes(path, VBytes.ToByteString(bytes));
        if (failure != "") return VError(failure);
        return 0;
    }

    // One-shot work is all done in the first Step. --watch waits for input changes in later Steps.
    public int Step()
    {
        if (_watchSession != null) return StepWatch();
        if (_o.Watch && _o.ReloadFrom.Count == 0 && !_o.Dump)
        {
            if (_o.Output == "") return VError("-o is required");
            ReadInputList();
            string watchClash = OutputsClash();
            if (watchClash != "") return VError(watchClash);
            string watchMissing = MissingFile(_o.Inputs);
            if (watchMissing != "") return VError("could not find file: " + watchMissing);
            return StartWatch();
        }
        var job = new TcsToVm();
        job.Entry = _o.Entry;
        job.Exports = _o.Exports;
        job.Traces = _o.Traces;
        job.DumpIl = _o.Dump;
        foreach (var p in _o.Inputs)
        {
            if (!job.Inputs.SourceFile(p)) return VError("could not find file: " + p);
        }
        string clash = _o.Dump ? "" : OutputsClash();
        if (clash != "") return VError(clash);
        foreach (var p in _o.Refs)
        {
            if (!job.Inputs.ReferenceFile(p)) return VError("could not find file: " + p);
        }
        if (!_o.Dump)
        {
            if (_o.Output == "") return VError("-o is required");
            if (_o.ReloadFrom.Count > 0)
            {
                // reload: builds the new module and the plan (--plan) from the old version (--reload-from) and the new version (docs/tcs/object-model.md §6)
                if (_o.PlanOut == "" && _o.BatchOut == "") return VError("--plan or --batch is required with --reload-from");
                foreach (var p in _o.ReloadFrom)
                {
                    if (!job.OldSources.SourceFile(p)) return VError("could not find file: " + p);
                }
            }
        }
        var r = job.Step()!;
        if (r.Failure != "") return VError(r.Failure);
        if (r.Failed) return VError("TinyC# diagnostics" + (r.OldVersion ? " (old version)" : "") + ":\n" + string.Join("\n", r.ErrorLines()));
        if (_o.Dump)
        {
            CliProcess.Out(r.Text);
            return 0;
        }
        if (WriteBytes(_o.Output, r.Module) != 0) return 1;
        if (_o.PlanOut != "" && WriteBytes(_o.PlanOut, r.Plan) != 0) return 1;
        if (_o.BatchOut != "" && WriteBytes(_o.BatchOut, r.Batch) != 0) return 1;
        return 0;
    }

    // ---- --watch (docs/tcs/vm-design.md §13.3): each time the inputs change, builds a reload from the previous build, rewrites -o, and then
    // writes a `@@tcs_<kind> <byte count>` line and the body to standard output. kind is vm_batch (the body is one line of the batch in base64 plus a newline;
    // the host applies it with tvm_apply_batch), restart_required (the body is newline-terminated reason lines; the host reloads -o), or
    // reload_notes (sent before vm_batch: changes applied live but worth reporting; the body is newline-terminated lines).
    // An edit with diagnostics is written to stderr, and the next edit is awaited. Later diffs are built from the last written build ----

    private string Stamp()
    {
        var parts = new List<string>();
        if (_o.InputsFrom != "") parts.Add(TcsFiles.Stamp(_o.InputsFrom));
        foreach (var p in _o.Inputs) parts.Add(TcsFiles.Stamp(p));
        return string.Join("|", parts);
    }

    // Reads the --inputs-from list and replaces _o.Inputs
    private void ReadInputList()
    {
        if (_o.InputsFrom == "") return;
        string? text = TcsFiles.Read(_o.InputsFrom);
        if (text == null) return;
        var list = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            string p = line.Trim();
            if (p != "") list.Add(p);
        }
        _o.Inputs = list;
    }

    // Writes diagnostics to stderr and to build_failed on the host stream (the body is newline-terminated diagnostic lines)
    private static void ReportErrors(List<string> errors)
    {
        CliProcess.Err("tcs vm: " + string.Join("\n", errors) + "\n");
        var body = new List<string>();
        foreach (var e in errors) body.Add(e.Replace("\n", " ") + "\n");
        HostMessage.Write("build_failed", string.Join("", body));
    }

    private int StartWatch()
    {
        var session = new TcsSession();
        session.Entry = _o.Entry;
        session.Exports = _o.Exports;
        session.Traces = _o.Traces;
        if (MissingFile(_o.Refs) == "")
        {
            foreach (var p in _o.Refs) session.Reference(TcsFiles.Read(p) ?? "");
        }
        _watchSession = session;
        _stamp = Stamp();
        CliProcess.Err("tcs vm: watching\n");
        return BuildWatched();
    }

    private int StepWatch()
    {
        CliProcess.Sleep(100);
        string now = Stamp();
        if (now == _stamp) return -1;
        ReadInputList();
        _stamp = Stamp();
        return BuildWatched();
    }

    private static TcsResult Run(TcsSession session)
    {
        session.Start();
        while (true)
        {
            var r = session.Step();
            if (r != null) return r;
        }
    }

    // Gives the session the listed files that currently exist (a file that vanished mid-save is read on the next change) and builds.
    // Until the first pass, only writes -o; after that, writes diffs to the host stream
    private int BuildWatched()
    {
        var session = _watchSession!;
        var present = new List<string>();
        var texts = new List<string>();
        foreach (var p in _o.Inputs)
        {
            string? text = TcsFiles.Read(p);
            if (text == null) continue;
            present.Add(p);
            texts.Add(text);
        }
        foreach (var old in _watched)
        {
            if (!present.Contains(old)) session.Remove(old);
        }
        _watched = present;
        for (int i = 0; i < present.Count; i++) session.Source(present[i], texts[i]);
        var r = Run(session);
        bool first = !_built;
        if (r.Failed)
        {
            ReportErrors(r.ErrorLines());
            return -1;
        }
        if (WriteBytes(_o.Output, r.Module) != 0) return -1;
        _built = true;
        if (first) return -1;
        if (r.Kind == "restart")
        {
            var body = new List<string>();
            foreach (var x in r.Reasons) body.Add(x + "\n");
            HostMessage.Write("restart_required", string.Join("", body));
        }
        else
        {
            // Notes are sent before applying (the host passes them in the next OnReload)
            if (r.Notes.Count > 0)
            {
                var notes = new List<string>();
                foreach (var x in r.Notes) notes.Add(x + "\n");
                HostMessage.Write("reload_notes", string.Join("", notes));
            }
            HostMessage.Write("vm_batch", VBytes.Base64(r.Batch) + "\n");
        }
        return -1;
    }
}
