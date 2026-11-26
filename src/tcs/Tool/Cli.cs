using System.Collections.Generic;

namespace Tcs;

// The tcs CLI (`tcs check`, `tcs c`, `tcs vm`).
// The host (C, .NET, wasm) calls Start once, then Step repeatedly until SetDone arrives.
// In the C build (tcs2c), each return from an entry is a GC frame boundary, so the compile advances only one step per Step
public static class Cli
{
    public const string Version = "0.1.0";

    // The current job (only one of them; tcs2c has no inheritance, so there is a field per type)
    private static CheckRun? _check;
    private static CRun? _cRun;
    private static VmRun? _vmRun;

    public static void Start()
    {
        var args = new List<string>();
        int n = TcsHost.ArgCount();
        for (int i = 0; i < n; i++) args.Add(TcsHost.Arg(i));
        Clear();
        int code = Begin(args);
        if (code >= 0) Finish(code);
    }

    public static void Step()
    {
        int code;
        if (_check != null) code = _check.Step();
        else if (_cRun != null) code = _cRun.Step();
        else if (_vmRun != null) code = _vmRun.Step();
        else
        {
            TcsHost.SetDone();
            return;
        }
        if (code >= 0) Finish(code);
    }

    private static void Clear()
    {
        _check = null;
        _cRun = null;
        _vmRun = null;
    }

    private static void Finish(int code)
    {
        Clear();
        TcsHost.SetExitCode(code);
        TcsHost.SetDone();
    }

    // The exit code if finished, -1 if continuing with Step
    private static int Begin(List<string> args)
    {
        if (args.Count > 0 && args[0] == "check") return BeginCheck(args);
        if (args.Count > 0 && args[0] == "c") return BeginC(args);
        if (args.Count > 0 && args[0] == "vm") return BeginVm(args);
        if (args.Count == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            TcsHost.WriteOut(Usage());
            return 0;
        }
        if (args.Count == 1 && args[0] == "--version")
        {
            TcsHost.WriteOut("tcs " + Version + "\n");
            return 0;
        }
        // Invocation without a subcommand (including no arguments)
        TcsHost.WriteErr(Usage());
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
        foreach (var p in o.InputPaths)
        {
            if (!TcsHost.FileExists(p)) return Error("file not found: " + p);
        }
        foreach (var p in o.RefPaths)
        {
            if (!TcsHost.FileExists(p)) return Error("ref file not found: " + p);
        }
        _check = new CheckRun(o);
        return -1;
    }

    // `tcs c`: C backend. Tool/CRun.cs
    private static int BeginC(List<string> args)
    {
        var o = new COptions();
        for (int i = 1; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "-h" || a == "--help")
            {
                TcsHost.WriteOut(COptions.Usage + "\n");
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

    // `tcs vm`: tcs VM .tcsb. Tool/VmRun.cs
    private static int BeginVm(List<string> args)
    {
        var o = new VmOptions();
        for (int i = 1; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "-h" || a == "--help")
            {
                TcsHost.WriteOut(VmOptions.Usage + "\n");
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
            if (p == output || TcsHost.SameFile(p, output)) return "output " + output + " is the input file " + p + " (not overwritten)";
        }
        foreach (var p in b)
        {
            if (p == output || TcsHost.SameFile(p, output)) return "output " + output + " is the input file " + p + " (not overwritten)";
        }
        return "";
    }

    public static int CError(string message)
    {
        TcsHost.WriteErr("tcs c: " + message + "\n");
        return 1;
    }

    public static int Error(string message)
    {
        TcsHost.WriteErr("Error: " + message + "\n");
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

    public static List<string> ReadAll(List<string> paths)
    {
        var texts = new List<string>();
        foreach (var p in paths) texts.Add(TcsHost.ReadFile(p));
        return texts;
    }

    public static bool IsWarning(string d)
    {
        return d.Contains(": warning ") || d.StartsWith("warning ");
    }
}

// Arguments of `tcs check`
public sealed class CheckOptions
{
    public List<string> InputPaths = new List<string>();
    public List<string> RefPaths = new List<string>();
}

// tcs check: builds and checks the IL (Session.CheckOnly). Step returns the exit code, or -1 to continue
public sealed class CheckRun
{
    private static readonly string[] LibraryPaths = "TinySystem/Linq.cs TinySystem/ValueTuple.cs".Split(' ');
    private Session? _session;
    private CheckOptions _o;

    public CheckRun(CheckOptions o)
    {
        _o = o;
    }

    public int Step()
    {
        if (_session == null)
        {
            var sources = Cli.ReadAll(_o.InputPaths);
            var session = new Session("");
            session.CheckOnly = true;
            for (int i = 0; i < _o.InputPaths.Count; i++) session.Add(_o.InputPaths[i], sources[i], 0);
            foreach (var r in Cli.ReadAll(_o.RefPaths)) session.Add("", r, 1);
            foreach (var lib in LibraryPaths) session.Add(lib, TcsHost.ToolFile("lib/" + lib), 2);
            session.Start();
            _session = session;
            return -1;
        }
        if (!_session.Step()) return -1;
        var result = _session.Result;
        bool failed = !result.Ok;
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var d in result.Diagnostics)
        {
            if (Cli.IsWarning(d)) warnings.Add(d);
            else errors.Add(d);
        }
        if (failed && errors.Count == 0) errors.Add("compile failed");
        foreach (var e in errors) TcsHost.WriteErr(e + "\n");
        foreach (var w in warnings) TcsHost.WriteErr(w + "\n");
        return errors.Count == 0 && warnings.Count == 0 ? 0 : 1;
    }
}
