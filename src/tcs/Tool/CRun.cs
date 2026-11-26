using System.Collections.Generic;
using TcsC;

namespace Tcs;

// Arguments of `tcs c`
public sealed class COptions
{
    public const string Usage = "usage: tcs c [--entry CLASS] [--digest-f32] [--lib [--vm-host]] [--ref STUB.cs] [-o OUTPUT.c] INPUT.cs [INPUT.cs ...]";
    public List<string> Inputs = new List<string>();
    public List<string> References = new List<string>();
    public string Output = "";
    public string? Entry;
    public bool DigestF32;
    public bool Lib;
    public bool VmHost;
}

// IL to C: advances the compile (writing out IL with generics monomorphized) one Session step at a time, then converts it to C with CEmitter when done
public sealed class CRun
{
    private COptions _o;
    private Session? _session;
    private CEmitter? _emitter;
    private List<string> _sources = new List<string>();

    public CRun(COptions o)
    {
        _o = o;
    }

    public int Step()
    {
        if (_emitter != null) return StepEmit();
        if (_session == null)
        {
            foreach (var p in _o.Inputs)
            {
                if (!TcsHost.FileExists(p)) return Cli.CError("could not find file: " + p);
            }
            // Do not overwrite an input with the output (stop without writing anything)
            string clash = Cli.InputClash(_o.Output, _o.Inputs, _o.References);
            if (clash != "") return Cli.CError(clash);
            var session = new Session("");
            session.ExportOnly = true;
            session.Specialize = true;
            for (int i = 0; i < _o.Inputs.Count; i++)
            {
                string text = TcsHost.ReadFile(_o.Inputs[i]);
                _sources.Add(text);
                session.Add(_o.Inputs[i], text, 0);
            }
            foreach (var r in _o.References) session.Add("", TcsHost.ReadFile(r), 1);
            session.Add("TinySystem/Linq.cs", TcsHost.ToolFile("lib/TinySystem/Linq.cs"), 2);
            session.Add("TinySystem/ValueTuple.cs", TcsHost.ToolFile("lib/TinySystem/ValueTuple.cs"), 2);
            session.Start();
            _session = session;
            return -1;
        }
        if (!_session.Step()) return -1;
        var result = _session.Result;
        var errors = new List<string>();
        foreach (var d in result.Diagnostics)
        {
            if (!Cli.IsWarning(d)) errors.Add(d);
        }
        if (result.Export == null || errors.Count > 0)
        {
            if (errors.Count == 0) errors.Add("export failed");
            return Cli.CError("TinyC# diagnostics:\n" + string.Join("\n", errors));
        }
        // Bodies and expressions that could not be turned into IL become null, and C would either stop at the caller with "no IL body" or silently use the default value for an initializer,
        // so stop here and print the reason and location (the same message as tcs vm and tcs check)
        if (result.Export.InternalErrors.Count > 0)
        {
            var internals = new List<string>();
            foreach (var ie in result.Export.InternalErrors) internals.Add("internal: " + ie);
            return Cli.CError("TinyC# diagnostics:\n" + string.Join("\n", internals));
        }
        var runtime = new Dictionary<string, string>();
        foreach (var name in CEmitter.RuntimeFileNames())
        {
            string text = TcsHost.ToolFile("runtime/" + name);
            if (text == "") return Cli.CError("TinyC# runtime file not found: runtime/" + name);
            runtime[name] = text;
        }
        CFail.Error = "";
        var emitter = new CEmitter(result.Export, _o.DigestF32, runtime);
        if (_o.VmHost) emitter.VmHostSurface = HostSurface();
        // The compile tree is no longer needed, so release it (it is collected at the next step boundary)
        _session = null;
        _emitter = emitter;
        if (!emitter.Begin(_o.Entry, _o.Lib)) return Cli.CError(CFail.Error);
        return -1;
    }

    // Advance C generation one class per step
    private int StepEmit()
    {
        var emitter = _emitter!;
        if (!emitter.StepMethods()) return -1;
        if (CFail.Failed) return Cli.CError(CFail.Error);
        string c = emitter.Finish();
        if (CFail.Failed) return Cli.CError(CFail.Error);
        if (_o.Output == "")
        {
            TcsHost.WriteOut(c);
            return 0;
        }
        string failure = TcsHost.WriteFileAtomic(_o.Output, c);
        if (failure != "") return Cli.CError(failure);
        return 0;
    }

    // Functions the C module exposes to the VM (public static methods of classes and interface methods of the types read from the input as --ref)
    private List<ExHostFunction> HostSurface()
    {
        var session = new Session("");
        session.ExportOnly = true;
        foreach (var text in _sources) session.Add("", text, 1);
        var export = session.Compile().Export;
        if (export == null) return new List<ExHostFunction>();
        return export.HostFunctions;
    }
}
