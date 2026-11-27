using System.Collections.Generic;

namespace Tcs;

// The tcs CLI (`tcs check`, `tcs c`, `tcs vm`).
// The program that runs it (Native/main.c, Dotnet/Program.cs) gives each command line argument to Arg, then calls Step until it returns the exit code.
// In the C build, each return from an entry is a GC frame boundary, so the compile advances only one step per Step
public static class Cli
{
    public const string Version = "0.1.0";

    private static List<string> _args = new List<string>();
    private static bool _begun;
    // The current job (only one of them; tcs has no inheritance, so there is a field per type)
    private static CheckRun? _check;
    private static CRun? _cRun;
    private static VmRun? _vmRun;

    // One command line argument (the executable name is not included)
    public static void Arg(string text)
    {
        _args.Add(text);
    }

    // The exit code when finished, -1 to continue
    public static int Step()
    {
        int code;
        if (!_begun)
        {
            _begun = true;
            code = Begin(_args);
        }
        else if (_check != null) code = _check.Step();
        else if (_cRun != null) code = _cRun.Step();
        else if (_vmRun != null) code = _vmRun.Step();
        else code = 0;
        if (code >= 0) Clear();
        return code;
    }

    private static void Clear()
    {
        _args = new List<string>();
        _begun = false;
        _check = null;
        _cRun = null;
        _vmRun = null;
    }

    // The exit code if finished, -1 if continuing with Step
    private static int Begin(List<string> args)
    {
        if (args.Count > 0 && args[0] == "check") return BeginCheck(args);
        if (args.Count > 0 && args[0] == "c") return BeginC(args);
        if (args.Count > 0 && args[0] == "vm") return BeginVm(args);
        if (args.Count == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            CliProcess.Out(Usage());
            return 0;
        }
        if (args.Count == 1 && args[0] == "--version")
        {
            CliProcess.Out("tcs " + Version + "\n");
            return 0;
        }
        // Invocation without a subcommand (including no arguments)
        CliProcess.Err(Usage());
        return 1;
    }

    private static int BeginCheck(List<string> args)
    {
        var o = new CheckOptions();
        for (int i = 1; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "--ref")
            {
                if (IsMissingOptionValue(args, i)) return Error("missing value for --ref");
                i++;
                o.RefPaths.Add(args[i]);
            }
            else if (!a.StartsWith("-")) o.InputPaths.Add(a);
            else return Error("unknown option for check: " + a);
        }
        if (o.InputPaths.Count == 0) return Error("no input file specified");
        var job = TcsCompile.Check();
        foreach (var p in o.InputPaths)
        {
            if (!job.Inputs.SourceFile(p)) return Error("file not found: " + p);
        }
        foreach (var p in o.RefPaths)
        {
            if (!job.Inputs.ReferenceFile(p)) return Error("ref file not found: " + p);
        }
        _check = new CheckRun(job);
        return -1;
    }

    // `tcs c`: C backend. Cli/CRun.cs
    private static int BeginC(List<string> args)
    {
        var o = new COptions();
        for (int i = 1; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "-h" || a == "--help")
            {
                CliProcess.Out(COptions.Usage + "\n");
                return 0;
            }
            if (a == "-o" || a == "--output" || a == "--entry" || a == "--ref")
            {
                if (i + 1 >= args.Count) return CError("missing value for " + a + "\n" + COptions.Usage);
                i++;
                if (a == "--entry") o.Entry = args[i];
                else if (a == "--ref") o.References.Add(args[i]);
                else o.Output = args[i];
            }
            else if (a == "--digest-f32") o.DigestF32 = true;
            else if (a == "--lib") o.Lib = true;
            else if (a == "--vm-host") o.VmHost = true;
            else if (a.StartsWith("-")) return CError("unknown option: " + a + "\n" + COptions.Usage);
            else o.Inputs.Add(a);
        }
        if (o.Inputs.Count == 0) return CError(COptions.Usage);
        _cRun = new CRun(o);
        return -1;
    }

    // `tcs vm`: tcs VM .tcsb. Cli/VmRun.cs
    private static int BeginVm(List<string> args)
    {
        var o = new VmOptions();
        for (int i = 1; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "-h" || a == "--help")
            {
                CliProcess.Out(VmOptions.Usage + "\n");
                return 0;
            }
            if (a == "-o" || a == "--entry" || a == "--export" || a == "--reload-from" || a == "--plan" || a == "--batch" || a == "--ref" || a == "--inputs-from" || a == "--trace")
            {
                if (i + 1 >= args.Count) return VmRun.VError("missing value for " + a + "\n" + VmOptions.Usage);
                i++;
                string v = args[i];
                if (a == "-o") o.Output = v;
                else if (a == "--entry") o.Entry = v;
                else if (a == "--export") o.Exports.Add(v);
                else if (a == "--reload-from") o.ReloadFrom.Add(v);
                else if (a == "--plan") o.PlanOut = v;
                else if (a == "--batch") o.BatchOut = v;
                else if (a == "--inputs-from") o.InputsFrom = v;
                else if (a == "--trace") o.Traces.Add(v);
                else o.Refs.Add(v);
            }
            else if (a == "--watch") o.Watch = true;
            else if (a == "--dump-il") o.Dump = true;
            else o.Inputs.Add(a);
        }
        _vmRun = new VmRun(o);
        return -1;
    }

    // If the output path is the same file as one of the inputs (a or b), the error message for it. "" otherwise
    public static string InputClash(string output, List<string> a, List<string> b)
    {
        if (output == "") return "";
        foreach (var p in a)
        {
            if (p == output || TcsFiles.SameFile(p, output)) return "output " + output + " is the input file " + p + " (not overwritten)";
        }
        foreach (var p in b)
        {
            if (p == output || TcsFiles.SameFile(p, output)) return "output " + output + " is the input file " + p + " (not overwritten)";
        }
        return "";
    }

    public static int CError(string message)
    {
        CliProcess.Err("tcs c: " + message + "\n");
        return 1;
    }

    public static int Error(string message)
    {
        CliProcess.Err("Error: " + message + "\n");
        return 1;
    }

    private static bool IsMissingOptionValue(List<string> args, int optionIndex)
    {
        return optionIndex + 1 >= args.Count || args[optionIndex + 1].StartsWith("-");
    }

    public static string Usage()
    {
        var lines = new List<string>();
        lines.Add("Usage: tcs check <input.cs> [input2.cs ...] [--ref <ref.cs>]");
        lines.Add("       tcs vm [options] <input.cs> ...     # tcs VM bytecode (.tcsb); see tcs vm --help");
        lines.Add("       tcs c [options] <input.cs> ...      # C source; see tcs c --help");
        lines.Add("       tcs --help");
        lines.Add("       tcs --version");
        lines.Add("       tcs check <input.cs>        # diagnostics only (exit 1 on any error or warning)");
        lines.Add("       --ref <file.cs>             # type-check only (host API stubs)");
        return string.Join("\n", lines) + "\n";
    }
}

// Arguments of `tcs check`
public sealed class CheckOptions
{
    public List<string> InputPaths = new List<string>();
    public List<string> RefPaths = new List<string>();
}

// tcs check: the checks of TcsCompile.Check. Step returns the exit code, or -1 to continue
public sealed class CheckRun
{
    private TcsCompile _job;

    public CheckRun(TcsCompile job)
    {
        _job = job;
    }

    public int Step()
    {
        var result = _job.Step();
        if (result == null) return -1;
        int warnings = 0;
        foreach (var d in result.Blocking) CliProcess.Err(d.ToString() + "\n");
        foreach (var d in result.Diagnostics)
        {
            if (d.IsError) continue;
            CliProcess.Err(d.ToString() + "\n");
            warnings++;
        }
        return result.Blocking.Count == 0 && warnings == 0 ? 0 : 1;
    }
}
