using System.Collections.Generic;
using TcsC;

namespace Tcs;

// `tcs c` as a job: C source in the result's Text.
// Step advances it: null while it continues, the result when it is done (the job does not keep the result). How much one Step does is not part of the contract.
public sealed class TcsToC
{
    public TcsInputs Inputs = new TcsInputs();
    // The class that has Main (null if there is only one Main)
    public string? Entry;
    // Library form: no main, entries for the host (tcs_entry_<Class>_<Method>)
    public bool Lib;
    // With Lib: glue that registers the public static methods as tcs VM host functions (docs/tcs/object-model.md §10)
    public bool VmHost;
    public bool DigestF32;

    private TcsCompile? _compile;
    private CEmitter? _emitter;
    private List<Diagnostic> _diagnostics = new List<Diagnostic>();

    public TcsResult? Step()
    {
        if (_emitter != null) return StepEmit();
        if (_compile == null)
        {
            _compile = TcsCompile.Il(true);
            _compile.Inputs = Inputs;
        }
        var compiled = _compile.Step();
        if (compiled == null) return null;
        // The compile tree is no longer needed, so release it (it is collected at the next step boundary)
        _compile = null;
        if (compiled.Failed) return compiled;
        // C has no way to decide the compliance errors that the tcs VM accepts, so any error stops
        var errors = new List<Diagnostic>();
        foreach (var d in compiled.Diagnostics)
        {
            if (d.IsError) errors.Add(d);
        }
        // Bodies and expressions that could not be turned into IL become null, and C would either stop at the caller with "no IL body" or silently use the default value for an initializer,
        // so stop here and report the reason and location (the same message as tcs vm and tcs check)
        if (errors.Count == 0)
        {
            foreach (var ie in compiled.Il!.InternalErrors) errors.Add(Diagnostic.Plain("internal: " + ie));
        }
        if (errors.Count > 0)
        {
            compiled.Kind = "error";
            compiled.Blocking = errors;
            compiled.Il = null;
            return compiled;
        }
        var runtime = new Dictionary<string, string>();
        foreach (var name in CEmitter.RuntimeFileNames())
        {
            string text = TcsFiles.Shipped("runtime/" + name) ?? "";
            if (text == "") return TcsResult.Fail("TinyC# runtime file not found: runtime/" + name);
            runtime[name] = text;
        }
        CFail.Error = "";
        var emitter = new CEmitter(compiled.Il!, DigestF32, runtime);
        if (VmHost) emitter.VmHostSurface = HostSurface();
        _diagnostics = compiled.Diagnostics;
        if (!emitter.Begin(Entry, Lib)) return Failed();
        _emitter = emitter;
        return null;
    }

    // Advance C generation one class per step
    private TcsResult? StepEmit()
    {
        var emitter = _emitter!;
        if (!emitter.StepMethods()) return null;
        _emitter = null;
        if (CFail.Failed) return Failed();
        var r = new TcsResult();
        r.Diagnostics = _diagnostics;
        r.Text = emitter.Finish();
        if (CFail.Failed) return Failed();
        return r;
    }

    private static TcsResult Failed()
    {
        var r = TcsResult.Fail(CFail.Error);
        CFail.Error = "";
        return r;
    }

    // Functions the C module exposes to the VM (public static methods of classes and interface methods of the types read from the input as --ref)
    private List<ExHostFunction> HostSurface()
    {
        var session = new Session("");
        session.ExportOnly = true;
        foreach (var text in Inputs.Texts) session.Add("", text, 1);
        var export = session.Compile().Export;
        if (export == null) return new List<ExHostFunction>();
        return export.HostFunctions;
    }
}
