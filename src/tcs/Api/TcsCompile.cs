using System.Collections.Generic;

namespace Tcs;

// The front end as a job: the checks of `tcs check`, or the IL that the backends read.
// Step advances it: null while it continues, the result when it is done (the job does not keep the result). How much one Step does is not part of the contract.
public sealed class TcsCompile
{
    public TcsInputs Inputs = new TcsInputs();
    // Check: the entry class to check ("" for none)
    public string Entry = "";

    private bool _check;
    private bool _specialize;
    private Session? _session;

    // The same checks as `tcs check`. Blocking is the errors; warnings stay in Diagnostics
    public static TcsCompile Check()
    {
        var job = new TcsCompile();
        job._check = true;
        return job;
    }

    // The IL. specialize makes generics monomorphic (what the backends read).
    // Compliance errors that the tcs VM accepts (VmAccepted) do not stop it: Il comes with them in Diagnostics
    public static TcsCompile Il(bool specialize)
    {
        var job = new TcsCompile();
        job._specialize = specialize;
        return job;
    }

    public TcsResult? Step()
    {
        if (_session == null)
        {
            _session = NewSession();
            _session.Start();
            return null;
        }
        if (!_session.Step()) return null;
        var compiled = _session.Result;
        _session = null;
        return Finish(compiled);
    }

    // Runs to the end in one call (no GC boundary in the C build)
    public TcsResult Run()
    {
        return Finish(NewSession().Compile());
    }

    private Session NewSession()
    {
        if (!_check) return IlSession(Inputs, _specialize);
        var session = new Session(Entry);
        session.CheckOnly = true;
        Inputs.AddTo(session);
        return session;
    }

    // A compile session that builds the IL of inputs
    public static Session IlSession(TcsInputs inputs, bool specialize)
    {
        var session = new Session("");
        session.ExportOnly = true;
        session.Specialize = specialize;
        inputs.AddTo(session);
        return session;
    }

    private TcsResult Finish(CompileResult compiled)
    {
        return _check ? CheckResult(compiled) : IlResult(compiled);
    }

    private static TcsResult CheckResult(CompileResult compiled)
    {
        var r = new TcsResult();
        foreach (var d in compiled.Diagnostics)
        {
            r.Diagnostics.Add(d);
            if (d.IsError) r.Blocking.Add(d);
        }
        if (!compiled.Ok && r.Blocking.Count == 0)
        {
            var failed = Diagnostic.Plain("compile failed");
            r.Diagnostics.Add(failed);
            r.Blocking.Add(failed);
        }
        if (r.Blocking.Count > 0) r.Kind = "error";
        return r;
    }

    // The result of an IL compile (also for a Session that a caller keeps across edits)
    public static TcsResult IlResult(CompileResult compiled)
    {
        var r = new TcsResult();
        bool stop = false;
        foreach (var d in compiled.Diagnostics)
        {
            r.Diagnostics.Add(d);
            if (!d.IsError) continue;
            r.Blocking.Add(d);
            if (!VmAccepted.Is(d.ToString())) stop = true;
        }
        if (compiled.Export == null || stop)
        {
            if (r.Blocking.Count == 0)
            {
                var failed = Diagnostic.Plain("export failed");
                r.Diagnostics.Add(failed);
                r.Blocking.Add(failed);
            }
            r.Kind = "error";
            return r;
        }
        r.Blocking = new List<Diagnostic>();
        r.Il = compiled.Export;
        return r;
    }
}
