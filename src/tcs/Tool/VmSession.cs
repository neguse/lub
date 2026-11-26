using System.Collections.Generic;
using TcsC;
using TcsVm;

namespace Tcs;

/// <summary>The result of one step of an incremental VM session. Kind is "module" (first build; load Module), "batch" (apply Batch with
/// tvm_apply_batch), "restart" (reload Module; Reasons gives the reasons), or "error" (Errors; the last build stays).</summary>
public sealed class VmUpdate
{
    public string Kind;
    public List<int> Module = new List<int>();
    public List<int> Batch = new List<int>();
    public List<string> Reasons = new List<string>();
    // For "batch", changes that were applied live but should be reported to the host (ReloadRules.Notes)
    public List<string> Notes = new List<string>();
    public List<string> Errors = new List<string>();

    public VmUpdate(string kind)
    {
        Kind = kind;
    }
}

/// <summary>Incremental VM session (docs/tcs/vm-design.md §13.3, V5): holds the last build that passed and returns a reload batch or
/// a restart for each edit. `tcs vm --watch` and the web playground worker use the same one. An edit with diagnostics does not change the last build.</summary>
public sealed class VmSession
{
    private string _entry;
    private List<string> _exports;
    private List<string> _refs;
    private ExportResult? _last;
    // Compile session kept across edits (files whose contents did not change are not parsed again)
    private Session? _session;
    // Type and static descriptions of the last build that passed (the old version for the next reload)
    private VmDescription? _lastDesc;
    // Paths of the inputs added to the session (to remove files that disappeared from the list from the session)
    private List<string> _paths = new List<string>();
    /// <summary>Path prefixes for --trace (set on VmTrace each time a module is built).</summary>
    public List<string> Traces = new List<string>();

    public VmSession(string entryClass, List<string> exports, List<string> referenceSources)
    {
        _entry = entryClass;
        _exports = exports;
        _refs = referenceSources;
    }

    /// <summary>Compiles the inputs into the IL that the VM accepts. If there are blocking diagnostics, returns null and puts them in diagnostics.</summary>
    public static ExportResult? Export(List<string> sources, List<string> paths, List<string> refs, List<string> diagnostics)
    {
        var session = new Session("");
        session.ExportOnly = true;
        session.Specialize = true;
        for (int i = 0; i < sources.Count; i++) session.Add(i < paths.Count ? paths[i] : "", sources[i], 0);
        foreach (var r in refs) session.Add("", r, 1);
        session.Add("TinySystem/Linq.cs", TcsHost.ToolFile("lib/TinySystem/Linq.cs"), 2);
        session.Add("TinySystem/ValueTuple.cs", TcsHost.ToolFile("lib/TinySystem/ValueTuple.cs"), 2);
        return VmEmitter.Exported(session.Compile(), diagnostics);
    }

    public VmUpdate Update(List<string> sources, List<string> paths)
    {
        var diags = new List<string>();
        if (_session == null)
        {
            _session = new Session("");
            _session.ExportOnly = true;
            _session.Specialize = true;
            foreach (var r in _refs) _session.Add("", r, 1);
            _session.Add("TinySystem/Linq.cs", TcsHost.ToolFile("lib/TinySystem/Linq.cs"), 2);
            _session.Add("TinySystem/ValueTuple.cs", TcsHost.ToolFile("lib/TinySystem/ValueTuple.cs"), 2);
        }
        var now = new List<string>();
        for (int i = 0; i < sources.Count; i++) now.Add(i < paths.Count && paths[i] != "" ? paths[i] : "#" + i);
        foreach (var old in _paths)
        {
            if (!now.Contains(old)) _session.Remove(old);
        }
        _paths = now;
        for (int i = 0; i < sources.Count; i++) _session.SetSource(now[i], sources[i]);
        var next = VmEmitter.Exported(_session.Compile(), diags);
        if (next == null)
        {
            var e = new VmUpdate("error");
            e.Errors = diags;
            return e;
        }
        CFail.Error = "";
        VmTrace.Prefixes = Traces;
        if (_last == null)
        {
            var module = VmEmitter.Emit(next, _entry, _exports, out VmDescription desc);
            if (CFail.Failed) return Failed();
            _last = next;
            _lastDesc = desc;
            var r = new VmUpdate("module");
            r.Module = module;
            return r;
        }
        var notes = ReloadRules.Notes(ReloadRules.TypesOf(_last), ReloadRules.TypesOf(next));
        var m = VmEmitter.EmitReload(_lastDesc!, next, _entry, _exports, RestartReasons(_last, next), ReinitStatics(_last, next), out List<int> plan, out List<string> reasons,
            out VmDescription nextDesc);
        if (CFail.Failed) return Failed();
        _last = next;
        _lastDesc = nextDesc;
        if (reasons.Count > 0)
        {
            var r = new VmUpdate("restart");
            r.Module = m;
            r.Reasons = reasons;
            return r;
        }
        var b = new VmUpdate("batch");
        b.Module = m;
        b.Batch = VmEmitter.Batch(m, plan);
        b.Notes = notes;
        return b;
    }

    /// <summary>Reasons the edit cannot be applied live (ReloadRules).</summary>
    public static List<string> RestartReasons(ExportResult oldProgram, ExportResult newProgram) =>
        ReloadRules.RestartReasons(ReloadRules.TypesOf(oldProgram), ReloadRules.TypesOf(newProgram));

    /// <summary>Statics that reload re-evaluates (ReloadRules).</summary>
    public static List<string> ReinitStatics(ExportResult oldProgram, ExportResult newProgram) =>
        ReloadRules.ReinitStatics(ReloadRules.TypesOf(oldProgram), ReloadRules.TypesOf(newProgram));

    private static VmUpdate Failed()
    {
        var e = new VmUpdate("error");
        e.Errors.Add(CFail.Error);
        CFail.Error = "";
        return e;
    }
}
