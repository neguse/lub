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
    private VmSession? _watchSession;
    private string _stamp = "";
    // Whether the first build under --watch passed
    private bool _built;

    public VmRun(VmOptions o)
    {
        _o = o;
    }

    private List<string>? ReadAll(List<string> paths)
    {
        var r = new List<string>();
        foreach (var p in paths)
        {
            if (!TcsHost.FileExists(p)) return null;
            r.Add(TcsHost.ReadFile(p));
        }
        return r;
    }

    private string MissingFile(List<string> paths)
    {
        foreach (var p in paths)
        {
            if (!TcsHost.FileExists(p)) return p;
        }
        return "";
    }

    public static int VError(string message)
    {
        TcsHost.WriteErr("tcs vm: " + message + "\n");
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
        string failure = TcsHost.WriteBinaryAtomic(path, VBytes.ToByteString(bytes));
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
            var watched = ReadAll(_o.Inputs);
            if (watched == null) return VError("could not find file: " + MissingFile(_o.Inputs));
            return StartWatch(watched);
        }
        var sources = ReadAll(_o.Inputs);
        if (sources == null) return VError("could not find file: " + MissingFile(_o.Inputs));
        string clash = _o.Dump ? "" : OutputsClash();
        if (clash != "") return VError(clash);
        var refs = ReadAll(_o.Refs);
        if (refs == null) return VError("could not find file: " + MissingFile(_o.Refs));
        var diags = new List<string>();
        var exported = VmSession.Export(sources, _o.Inputs, refs, diags);
        if (exported == null) return VError("TinyC# diagnostics:\n" + string.Join("\n", diags));
        if (_o.Dump)
        {
            TcsHost.WriteOut(IlProgramText(exported));
            return 0;
        }
        if (_o.Output == "") return VError("-o is required");
        CFail.Error = "";
        VmTrace.Prefixes = _o.Traces;
        if (_o.ReloadFrom.Count > 0)
        {
            // reload: builds the new module and the plan (--plan) from the old version (--reload-from) and the new version (docs/tcs/object-model.md §6)
            if (_o.PlanOut == "" && _o.BatchOut == "") return VError("--plan or --batch is required with --reload-from");
            var oldSources = ReadAll(_o.ReloadFrom);
            if (oldSources == null) return VError("could not find file: " + MissingFile(_o.ReloadFrom));
            var oldDiags = new List<string>();
            var old = VmSession.Export(oldSources, _o.ReloadFrom, refs, oldDiags);
            if (old == null) return VError("TinyC# diagnostics (old version):\n" + string.Join("\n", oldDiags));
            var module = VmEmitter.EmitReload(old, exported, _o.Entry, _o.Exports, VmSession.RestartReasons(old, exported), VmSession.ReinitStatics(old, exported), out List<int> plan, out List<string> reasons);
            if (CFail.Failed) return VError(CFail.Error);
            if (WriteBytes(_o.Output, module) != 0) return 1;
            if (_o.PlanOut != "" && WriteBytes(_o.PlanOut, plan) != 0) return 1;
            if (_o.BatchOut != "" && WriteBytes(_o.BatchOut, VmEmitter.Batch(module, plan)) != 0) return 1;
            return 0;
        }
        var bytes = VmEmitter.Emit(exported, _o.Entry, _o.Exports);
        if (CFail.Failed) return VError(CFail.Error);
        return WriteBytes(_o.Output, bytes);
    }

    // ---- --watch (docs/tcs/vm-design.md §13.3): each time the inputs change, builds a reload from the previous build, rewrites -o, and then
    // writes a `@@tcs_<kind> <byte count>` line and the body to standard output. kind is vm_batch (the body is one line of the batch in base64 plus a newline;
    // the host applies it with tvm_apply_batch), restart_required (the body is newline-terminated reason lines; the host reloads -o), or
    // reload_notes (sent before vm_batch: changes applied live but worth reporting; the body is newline-terminated lines).
    // An edit with diagnostics is written to stderr, and the next edit is awaited. Later diffs are built from the last written build ----

    private string Stamp()
    {
        var parts = new List<string>();
        if (_o.InputsFrom != "") parts.Add(TcsHost.FileStamp(_o.InputsFrom));
        foreach (var p in _o.Inputs) parts.Add(TcsHost.FileStamp(p));
        return string.Join("|", parts);
    }

    // Reads the --inputs-from list and replaces _o.Inputs
    private void ReadInputList()
    {
        if (_o.InputsFrom == "" || !TcsHost.FileExists(_o.InputsFrom)) return;
        var list = new List<string>();
        foreach (var line in TcsHost.ReadFile(_o.InputsFrom).Split('\n'))
        {
            string p = line.Trim();
            if (p != "") list.Add(p);
        }
        _o.Inputs = list;
    }

    // Reads only the listed files that currently exist (a file that vanished mid-save is read on the next change)
    private void ReadPresent(List<string> sources, List<string> paths)
    {
        foreach (var p in _o.Inputs)
        {
            if (!TcsHost.FileExists(p)) continue;
            sources.Add(TcsHost.ReadFile(p));
            paths.Add(p);
        }
    }

    // Writes diagnostics to stderr and to build_failed on the host stream (the body is newline-terminated diagnostic lines)
    private static void ReportErrors(List<string> errors)
    {
        TcsHost.WriteErr("tcs vm: " + string.Join("\n", errors) + "\n");
        var body = new List<string>();
        foreach (var e in errors) body.Add(e.Replace("\n", " ") + "\n");
        HostMessage.Write("build_failed", string.Join("", body));
    }

    private int StartWatch(List<string> sources)
    {
        var refs = ReadAll(_o.Refs);
        _watchSession = new VmSession(_o.Entry, _o.Exports, refs == null ? new List<string>() : refs);
        _watchSession.Traces = _o.Traces;
        _stamp = Stamp();
        TcsHost.WriteErr("tcs vm: watching\n");
        return BuildWatched(sources, _o.Inputs);
    }

    private int StepWatch()
    {
        TcsHost.Sleep(100);
        string now = Stamp();
        if (now == _stamp) return -1;
        ReadInputList();
        _stamp = Stamp();
        var sources = new List<string>();
        var paths = new List<string>();
        ReadPresent(sources, paths);
        return BuildWatched(sources, paths);
    }

    // Until the first pass, only writes -o; after that, writes diffs to the host stream
    private int BuildWatched(List<string> sources, List<string> paths)
    {
        bool first = !_built;
        var r = _watchSession!.Update(sources, paths);
        if (r.Kind == "error")
        {
            ReportErrors(r.Errors);
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

    // ---- --dump-il: IL text for diagnosis (non-normative) ----

    public static string IlProgramText(ExportResult program)
    {
        var lines = new List<string>();
        foreach (var c in program.Classes)
        {
            lines.Add("class " + c.Name);
            foreach (var f in c.Fields)
                lines.Add("  field " + (f.IsStatic ? "static " : "") + f.Name + ": " + f.Type + " = " + IlNodes.Dump(f.Init));
            foreach (var k in c.Ctors)
            {
                var ps = new List<string>();
                for (int i = 0; i < k.Parameters.Count && i < k.ParameterTypes.Count; i++) ps.Add(k.Parameters[i] + ": " + k.ParameterTypes[i]);
                lines.Add("  ctor " + k.Name + "(" + string.Join(", ", ps) + ")");
                if (k.Body != null) BlockLines(lines, k.Body, 2);
            }
            foreach (var m in c.Methods)
            {
                var ps = new List<string>();
                for (int i = 0; i < m.Parameters.Count; i++) ps.Add(m.Parameters[i] + ": " + (i < m.ParameterTypes.Count ? m.ParameterTypes[i] : "?"));
                lines.Add("  method " + (m.IsStatic ? "static " : "") + m.Name + "(" + string.Join(", ", ps) + "): " + m.ReturnType);
                if (m.Body != null) BlockLines(lines, m.Body, 2);
            }
        }
        foreach (var s in program.Structs)
        {
            lines.Add("struct " + s.Name);
            foreach (var f in s.Fields) lines.Add("  field " + f.Name + ": " + f.Type);
        }
        if (program.TopLevel != null)
        {
            lines.Add("toplevel");
            BlockLines(lines, program.TopLevel, 1);
        }
        var parts = new List<string>();
        foreach (var l in lines) parts.Add(l + "\n");
        return string.Join("", parts);
    }

    private static void BlockLines(List<string> lines, IlBlock b, int depth)
    {
        string indent = "";
        for (int i = 0; i < depth; i++) indent = indent + "  ";
        foreach (var s in b.Stats) lines.Add(indent + IlNodes.Dump(s));
    }
}
