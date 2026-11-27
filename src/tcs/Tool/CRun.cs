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

// tcs c: the job of TcsToC. Step returns the exit code, or -1 to continue
public sealed class CRun
{
    private COptions _o;
    private TcsToC? _job;

    public CRun(COptions o)
    {
        _o = o;
    }

    public int Step()
    {
        if (_job == null)
        {
            foreach (var p in _o.Inputs)
            {
                if (!TcsHost.FileExists(p)) return Cli.CError("could not find file: " + p);
            }
            // Do not overwrite an input with the output (stop without writing anything)
            string clash = Cli.InputClash(_o.Output, _o.Inputs, _o.References);
            if (clash != "") return Cli.CError(clash);
            var job = new TcsToC();
            job.Entry = _o.Entry;
            job.Lib = _o.Lib;
            job.VmHost = _o.VmHost;
            job.DigestF32 = _o.DigestF32;
            foreach (var p in _o.Inputs) job.Inputs.SourceFile(p);
            foreach (var r in _o.References) job.Inputs.Reference(TcsHost.ReadFile(r));
            _job = job;
        }
        var result = _job.Step();
        if (result == null) return -1;
        if (result.Failure != "") return Cli.CError(result.Failure);
        if (result.Failed) return Cli.CError("TinyC# diagnostics:\n" + string.Join("\n", result.ErrorLines()));
        if (_o.Output == "")
        {
            TcsHost.WriteOut(result.Text);
            return 0;
        }
        string failure = TcsHost.WriteFileAtomic(_o.Output, result.Text);
        if (failure != "") return Cli.CError(failure);
        return 0;
    }
}
