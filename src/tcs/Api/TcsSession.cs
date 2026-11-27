using System.Collections.Generic;
using TcsC;
using TcsVm;

namespace Tcs;

// A tcs VM build that continues across edits (docs/tcs/vm-design.md §13.3): keeps the last build that passed, and for each edit returns
// Kind "module" (the first build; load Module), "batch" (apply Batch with tvm_apply_batch; Module is the same version as a module),
// "restart" (reload Module; Reasons says why) or "error" (the last build stays).
// Change the sources with Source and Remove, then Start, then Step: null while it continues, the result when it is done (the session does not keep the result).
// How much one Step does is not part of the contract.
// Files whose text did not change are not parsed again.
public sealed class TcsSession
{
    // The class that has Main ("" if there is only one Main)
    public string Entry = "";
    // Functions to build besides the entry (`C.M`, or `C.*` for all static methods of class C)
    public List<string> Exports = new List<string>();
    // Path prefixes of the files whose statements and assignments are recorded
    public List<string> Traces = new List<string>();

    private List<string> _references = new List<string>();
    private Session _session;
    private bool _open;
    private ExportResult? _last;
    // Type and static descriptions of the last build that passed (the old version for the next reload)
    private VmDescription? _lastDesc;

    public TcsSession()
    {
        _session = new Session("");
        _session.ExportOnly = true;
        _session.Specialize = true;
    }

    // Declarations that are only type-checked, like --ref. Given before the first Start
    public void Reference(string text)
    {
        _references.Add(text);
    }

    // Adds a source, or replaces its text. name is the path shown in diagnostics and is what Remove takes (not "")
    public void Source(string name, string text)
    {
        Open();
        _session.SetSource(name, text);
    }

    public void Remove(string name)
    {
        _session.Remove(name);
    }

    // Begins a build of the current sources
    public void Start()
    {
        Open();
    }

    public TcsResult? Step()
    {
        return Build();
    }

    // References and the library go in before the first source
    private void Open()
    {
        if (_open) return;
        _open = true;
        TcsInputs.AddReferences(_session, _references);
    }

    private TcsResult Build()
    {
        var r = new TcsResult();
        var next = TcsToVm.Accept(_session.Compile(), r);
        if (next == null) return r;
        CFail.Error = "";
        VmTrace.Prefixes = Traces;
        if (_last == null)
        {
            r.Module = VmEmitter.Emit(next, Entry, Exports, out VmDescription desc);
            if (CFail.Failed) return TcsToVm.Failed();
            _last = next;
            _lastDesc = desc;
            r.Kind = "module";
            return r;
        }
        var notes = ReloadRules.Notes(ReloadRules.TypesOf(_last), ReloadRules.TypesOf(next));
        r.Module = VmEmitter.EmitReload(_lastDesc!, next, Entry, Exports, TcsToVm.RestartReasons(_last, next), TcsToVm.ReinitStatics(_last, next), out List<int> plan, out List<string> reasons,
            out VmDescription nextDesc);
        if (CFail.Failed) return TcsToVm.Failed();
        _last = next;
        _lastDesc = nextDesc;
        if (reasons.Count > 0)
        {
            r.Kind = "restart";
            r.Reasons = reasons;
            return r;
        }
        r.Kind = "batch";
        r.Plan = plan;
        r.Batch = VmEmitter.Batch(r.Module, plan);
        r.Notes = notes;
        return r;
    }
}
