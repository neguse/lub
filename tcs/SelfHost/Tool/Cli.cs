using System.Collections.Generic;

namespace TcsSelf;

// tcs の CLI (`tcs <input.cs> ... -o out.lua`、`tcs check`、`tcs --map-stacktrace`)。
// host (C、Lua、.NET) は Start を 1 回、SetDone が来るまで Step を繰り返し呼ぶ。
// C の build (tcs2c) は entry から戻るたびに GC の frame の境界になるので、compile は Step ごとに 1 段だけ進める
public static class Cli
{
    public const string Version = "0.1.0";

    // 今の仕事 (どれか 1 つ。tcs2c は継承を持たないので型ごとに持つ)
    private static CheckRun? _check;
    private static OnceRun? _once;
    private static WatchRun? _watch;
    private static SnapshotRun? _snapshot;
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
        else if (_once != null) code = _once.Step();
        else if (_watch != null) code = _watch.Step();
        else if (_snapshot != null) code = _snapshot.Step();
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
        _once = null;
        _watch = null;
        _snapshot = null;
        _cRun = null;
        _vmRun = null;
    }

    private static void Finish(int code)
    {
        Clear();
        TcsHost.SetExitCode(code);
        TcsHost.SetDone();
    }

    // 終わったなら終了コード、Step で続けるなら -1
    private static int Begin(List<string> args)
    {
        if (args.Count > 0 && args[0] == "--map-stacktrace") return MapStackTrace(args);
        if (args.Count > 0 && args[0] == "check") return BeginCheck(args);
        if (args.Count > 0 && args[0] == "c") return BeginC(args);
        if (args.Count > 0 && args[0] == "vm") return BeginVm(args);
        if (args.Count == 0)
        {
            TcsHost.WriteErr(Usage());
            return 1;
        }
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
        var o = new CliOptions();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "-o" || a == "--ref" || a == "--entry" || a == "--prelude" || a == "--trace")
            {
                if (IsMissingOptionValue(args, i)) return Error("missing value for " + a);
                i++;
                string v = args[i];
                if (a == "-o") o.OutputPath = v;
                else if (a == "--ref") o.RefPaths.Add(v);
                else if (a == "--entry") o.EntryClass = v;
                else if (a == "--prelude") o.PreludePath = v;
                else o.TracePrefixes.Add(LuaGen.NormalizeTracePath(v));
            }
            else if (a == "--sourcemap") o.EmitSourceMap = true;
            else if (a == "--watch" || a == "-w") o.Watch = true;
            else if (a == "--no-runtime") o.IncludeRuntime = false;
            else if (a == "--snapshot") o.Snapshot = true;
            else if (a == "--module") o.Module = true;
            else if (a == "--instance-registry") o.InstanceRegistry = true;
            else if (a == "--reload-chunks") o.ReloadChunks = true;
            else if (a == "--warnaserror") o.WarningsAsErrors = true;
            else if (!a.StartsWith("-")) o.InputPaths.Add(a);
            else return Error("unknown option: " + a);
        }
        if (o.Module && (o.EntryClass != "" || o.Snapshot)) return Error("--module cannot be combined with --entry/--snapshot");
        if (o.Snapshot)
        {
            if (o.EntryClass == "") return Error("--snapshot requires --entry <Class>");
            if (o.PreludePath != "" || o.EmitSourceMap || !o.IncludeRuntime || o.Watch || o.InstanceRegistry || o.TracePrefixes.Count > 0)
                return Error("--snapshot cannot be combined with --prelude/--sourcemap/--no-runtime/--watch/--instance-registry/--trace");
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
        if (o.PreludePath != "" && !TcsHost.FileExists(o.PreludePath)) return Error("prelude file not found: " + o.PreludePath);
        if (o.Watch && o.OutputPath == "") return Error("--watch requires -o <output.lua>");
        if (o.ReloadChunks && !o.Watch) return Error("--reload-chunks requires --watch");
        // reload chunk は v1 の instance registry を前提にする
        if (o.ReloadChunks) o.InstanceRegistry = true;
        string conflict = OutputPathConflict(o);
        if (conflict != "") return Error(conflict);
        if (o.Snapshot)
        {
            _snapshot = new SnapshotRun(o);
            return -1;
        }
        if (o.Watch)
        {
            _watch = new WatchRun(o);
            return -1;
        }
        _once = new OnceRun(o);
        return -1;
    }

    private static int BeginCheck(List<string> args)
    {
        var o = new CliOptions();
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

    // `tcs c`: C backend (旧 tcs2c と同じ引数)。SelfHost/Tool/CRun.cs
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

    // `tcs vm`: tcs VM の .tcsb (旧 tcs2vm と同じ引数)。SelfHost/Tool/VmRun.cs
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
            if (a == "-o" || a == "--entry" || a == "--export" || a == "--reload-from" || a == "--plan" || a == "--batch" || a == "--ref" || a == "--inputs-from")
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
                else o.Refs.Add(v);
            }
            else if (a == "--watch") o.Watch = true;
            else if (a == "--dump-il") o.Dump = true;
            else o.Inputs.Add(a);
        }
        _vmRun = new VmRun(o);
        return -1;
    }

    // 旧 tcs2c の誤りの形 (`tcs2c: ...`)
    public static int CError(string message)
    {
        TcsHost.WriteErr("tcs2c: " + message + "\n");
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
        lines.Add("Usage: tcs <input.cs> [input2.cs ...] [--ref <ref.cs>] [-o <output.lua>] [--entry <Class>] [--module] [--prelude <shim.lua>] [--sourcemap] [--watch] [--no-runtime] [--instance-registry] [--reload-chunks] [--warnaserror] [--trace <path-prefix>]...");
        lines.Add("       tcs check <input.cs> [input2.cs ...] [--ref <ref.cs>]");
        lines.Add("       tcs --map-stacktrace <output.lua.map> [trace.txt]");
        lines.Add("       tcs --help");
        lines.Add("       tcs --version");
        lines.Add("       tcs <input.cs>              # prints to stdout");
        lines.Add("       tcs check <input.cs>        # diagnostics only, no Lua output");
        lines.Add("       --ref <file.cs>             # type-check only (no Lua output)");
        lines.Add("       --entry <Class>             # append 'return <Class>' so the output loads as a Lua module");
        lines.Add("       --warnaserror               # fail without writing output when there are warnings (same rule as tcs check)");
        lines.Add("       --prelude <shim.lua>        # prepend a user Lua file (host API shim, etc.) to the output");
        lines.Add("       --no-runtime                # omit embedded TinySystem runtime prelude");
        lines.Add("       --snapshot                  # emit module-registry bridge snapshot (requires --entry)");
        lines.Add("       --module                    # append 'return { Type = Type, ... }' so require() gets the defined types");
        lines.Add("       --instance-registry         # register instances in the weak __tcs_instances table for hot reload migration (dev only)");
        lines.Add("       --reload-chunks             # with --watch: on each rebuild also write to stdout a chunk that applies the change to a VM running the previous build, or a restart request with the reasons when the change cannot be applied live (implies --instance-registry)");
        lines.Add("       --trace <path-prefix>       # record executed C# lines of inputs under the prefix in the global table __tcs_trace (keys \"<file>:<line>\") and keep the current line in the local __tcs_at of each Lua function (dev only; repeatable)");
        return string.Join("\n", lines) + "\n";
    }

    // 出力 (と source map) の path が入力、--ref、prelude と同じファイルを指すなら、その理由 (無ければ "")
    public static string OutputPathConflict(CliOptions o)
    {
        if (o.OutputPath == "") return "";
        var roles = new List<string>();
        var paths = new List<string>();
        foreach (var p in o.InputPaths)
        {
            roles.Add("input");
            paths.Add(p);
        }
        foreach (var p in o.RefPaths)
        {
            roles.Add("ref");
            paths.Add(p);
        }
        if (o.PreludePath != "")
        {
            roles.Add("prelude");
            paths.Add(o.PreludePath);
        }
        var keys = new List<string>();
        var ids = new List<string>();
        for (int i = 0; i < paths.Count; i++)
        {
            string id = TcsHost.FileIdentity(paths[i]);
            if (id == "") return "cannot validate output path: protected " + roles[i] + " disappeared: " + paths[i];
            keys.Add(TcsHost.PathKey(paths[i]));
            ids.Add(id);
        }
        var writeRoles = new List<string>();
        var writePaths = new List<string>();
        writeRoles.Add("output");
        writePaths.Add(o.OutputPath);
        if (o.EmitSourceMap)
        {
            writeRoles.Add("source map");
            writePaths.Add(o.OutputPath + ".map");
        }
        for (int w = 0; w < writePaths.Count; w++)
        {
            string key = TcsHost.PathKey(writePaths[w]);
            string id = TcsHost.FileIdentity(writePaths[w]);
            for (int i = 0; i < keys.Count; i++)
            {
                if (key == keys[i] || id != "" && id != "-" && id == ids[i])
                    return writeRoles[w] + " path conflicts with " + roles[i] + ": " + writePaths[w];
            }
        }
        return "";
    }

    // --map-stacktrace <map> [trace]: Lua の stack trace の行に C# の位置を足す
    private static int MapStackTrace(List<string> args)
    {
        if (args.Count < 2 || args.Count > 3)
        {
            TcsHost.WriteErr("Usage: tcs --map-stacktrace <output.lua.map> [trace.txt]\n");
            TcsHost.WriteErr("       If trace.txt is omitted, stack trace is read from stdin.\n");
            return 1;
        }
        string mapPath = args[1];
        if (!TcsHost.FileExists(mapPath)) return Error("source map not found: " + mapPath);
        string json = TcsHost.ReadFile(mapPath);
        var map = SourceMapText.Parse(json);
        if (map == null) return Error("invalid source map: " + mapPath);
        string trace = args.Count == 3 ? TcsHost.ReadFile(args[2]) : TcsHost.ReadStdin();
        TcsHost.WriteOut(SourceMapText.AnnotateStackTrace(trace, map));
        return 0;
    }

    // ---- compile ----

    private static readonly string[] LibraryPaths = "TinySystem/Linq.cs TinySystem/ValueTuple.cs".Split(' ');

    // 入力を読んで session を作る (compile は Step で進める)
    public static Session NewSession(CliOptions o, List<string> sources, List<string> refSources)
    {
        var session = new Session(o.EntryClass);
        session.InstanceRegistry = o.InstanceRegistry;
        session.Module = o.Module;
        session.TracePrefixes = o.TracePrefixes;
        for (int i = 0; i < o.InputPaths.Count; i++) session.Add(o.InputPaths[i], sources[i], 0);
        foreach (var r in refSources) session.Add("", r, 1);
        foreach (var lib in LibraryPaths) session.Add(lib, TcsHost.ToolFile("lib/" + lib), 2);
        session.Start();
        return session;
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

    // tcs と一緒に配る runtime のファイル。無ければ null (呼ぶ側が "runtime file not found" で止める)
    public static string? RuntimeFile(string name)
    {
        string text = TcsHost.ToolFile(name);
        if (text == "")
        {
            Error("TinyC# runtime file not found: " + name);
            return null;
        }
        return text;
    }

    // 出力の前置き: --prelude、次に埋め込みの runtime。source map の Lua の行を足した前置きの行の数だけずらす。runtime が無ければ null
    public static ComposedOutput? Compose(string lua, CliOptions o)
    {
        int offset = 0;
        if (o.PreludePath != "")
        {
            string prelude = TcsHost.ReadFile(o.PreludePath);
            if (!prelude.EndsWith("\n")) prelude = prelude + "\n";
            lua = prelude + lua;
            offset = offset + CountLines(prelude);
        }
        if (o.IncludeRuntime)
        {
            var runtime = RuntimeFile("runtime/tinysystem.lua");
            if (runtime == null) return null;
            string embedded = EmbeddedRuntime(runtime);
            lua = embedded + lua;
            offset = offset + CountLines(embedded);
        }
        return new ComposedOutput(lua, offset);
    }

    // runtime の Lua を `TinySystem` の global と別名に包む (生成コードは素の名前で引く)
    public static string EmbeddedRuntime(string runtimeSource)
    {
        var parts = new List<string>();
        parts.Add("-- Generated by TinyC# transpiler\n");
        parts.Add("-- TinyC# embedded runtime prelude\n");
        parts.Add("do\n");
        parts.Add("  local TinySystem = (function()\n");
        string normalized = TrimEnd(runtimeSource.Replace("\r\n", "\n"));
        foreach (var line in normalized.Split('\n')) parts.Add("    " + line + "\n");
        parts.Add("  end)()\n");
        parts.Add("  _G.TinySystem = TinySystem\n");
        foreach (var name in RuntimeGlobalAliases) parts.Add("  _G." + name + " = TinySystem." + name + "\n");
        parts.Add("end\n");
        parts.Add("\n");
        return string.Join("", parts);
    }

    // 生成コードが素の global の名前で参照する runtime の table (.NET の LuaRuntime.RuntimeGlobalAliases と同じ)
    public static readonly string[] RuntimeGlobalAliases = "List Dict Math String Random Char TcsQueue TcsHashSet".Split(' ');

    public static int CountLines(string s)
    {
        var u = new TinySystem.Text.Utf8Bytes(s);
        int n = 0;
        for (int i = 0; i < u.Length; i++)
        {
            if (u[i] == 10) n++;
        }
        return n;
    }

    public static string TrimEnd(string s)
    {
        var u = new TinySystem.Text.Utf8Bytes(s);
        int n = u.Length;
        while (n > 0)
        {
            int c = u[n - 1];
            if (c == 32 || c == 10 || c == 13 || c == 9 || c == 11 || c == 12) n--;
            else break;
        }
        return u.Slice(0, n).ToString();
    }
}

public sealed class ComposedOutput
{
    public string Lua;
    public int LineOffset;

    public ComposedOutput(string lua, int lineOffset)
    {
        Lua = lua;
        LineOffset = lineOffset;
    }
}

public sealed class CliOptions
{
    public List<string> InputPaths = new List<string>();
    public List<string> RefPaths = new List<string>();
    public string OutputPath = "";
    public string EntryClass = "";
    public string PreludePath = "";
    public bool EmitSourceMap;
    public bool Watch;
    public bool IncludeRuntime = true;
    public bool Snapshot;
    public bool Module;
    public bool InstanceRegistry;
    public bool ReloadChunks;
    public bool WarningsAsErrors;
    public List<string> TracePrefixes = new List<string>();
}

// Step で進める仕事 (CheckRun、OnceRun、WatchRun、SnapshotRun。Step の戻りは終了コード、続けるなら -1)
public sealed class CheckRun
{
    private Session? _session;
    private CliOptions _o;

    public CheckRun(CliOptions o)
    {
        _o = o;
    }

    public int Step()
    {
        if (_session == null)
        {
            _session = Cli.NewSession(_o, Cli.ReadAll(_o.InputPaths), Cli.ReadAll(_o.RefPaths));
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

public sealed class OnceRun
{
    private Session? _session;
    private CliOptions _o;

    public OnceRun(CliOptions o)
    {
        _o = o;
    }

    public int Step()
    {
        if (_session == null)
        {
            _session = Cli.NewSession(_o, Cli.ReadAll(_o.InputPaths), Cli.ReadAll(_o.RefPaths));
            return -1;
        }
        if (!_session.Step()) return -1;
        var result = _session.Result;
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var d in result.Diagnostics)
        {
            if (Cli.IsWarning(d)) warnings.Add(d);
            else errors.Add(d);
        }
        if (!result.Ok || errors.Count > 0)
        {
            if (errors.Count == 0) errors.Add("compile failed");
            foreach (var e in errors) TcsHost.WriteErr(e + "\n");
            return 1;
        }
        foreach (var w in warnings) TcsHost.WriteErr(w + "\n");
        // tcs check と同じ判定 (警告が 1 つでもあれば失敗) を、同じ compile で出す
        if (_o.WarningsAsErrors && warnings.Count > 0) return 1;
        var composed = Cli.Compose(result.Lua, _o);
        if (composed == null) return 1;
        if (_o.OutputPath == "")
        {
            TcsHost.WriteOut(composed.Lua);
            if (_o.EmitSourceMap)
            {
                TcsHost.WriteErr("--- sourcemap ---\n");
                TcsHost.WriteErr(SourceMapText.ToJson(result.SourceMap, composed.LineOffset) + "\n");
            }
            return 0;
        }
        string conflict = Cli.OutputPathConflict(_o);
        if (conflict != "") return Cli.Error(conflict);
        string failure = TcsHost.WriteFileAtomic(_o.OutputPath, composed.Lua);
        if (failure != "") return Cli.Error(failure);
        TcsHost.WriteErr("Wrote " + _o.OutputPath + "\n");
        if (_o.EmitSourceMap)
        {
            string mapPath = _o.OutputPath + ".map";
            failure = TcsHost.WriteFileAtomic(mapPath, SourceMapText.ToJson(result.SourceMap, composed.LineOffset));
            if (failure != "") return Cli.Error(failure);
            TcsHost.WriteErr("Wrote " + mapPath + "\n");
        }
        return 0;
    }
}

// --watch: 入力 (と --ref、--prelude) を見張り、変わったら変換し直して -o を書き直す。
// --reload-chunks なら、動いている host の VM に当てる chunk か、当てられない理由を標準出力に書く
public sealed class WatchRun
{
    private const int Idle = 0;
    private const int Compiling = 1;
    private const int Exporting = 2;
    private const int ChunkCompiling = 3;

    private CliOptions _o;
    private List<string> _watchPaths = new List<string>();
    private List<string> _stamps = new List<string>();
    private int _phase = Idle;
    private bool _started;
    // 成功した build の通し番号と、host の VM が実行している build (reload chunk はここからの差分で作る)
    private int _lastBuild;
    private int _runningBuild;
    private ExportResult? _runningExport;
    private bool _hasRunning;
    // 進めている build
    private Session? _session;
    private List<string> _sources = new List<string>();
    private List<string> _refSources = new List<string>();
    private string _lua = "";
    private int _mapOffset;
    private int _build;
    private List<SourceMapEntry> _sourceMap = new List<SourceMapEntry>();
    private ExportResult? _newExport;
    private List<string> _restartReasons = new List<string>();
    private string _chunk = "";
    private bool _planned;
    private bool _testFailurePending;

    public WatchRun(CliOptions o)
    {
        _o = o;
        foreach (var p in o.InputPaths) _watchPaths.Add(p);
        foreach (var p in o.RefPaths) _watchPaths.Add(p);
        // 出力に埋め込む prelude も build の入力として見張る
        if (o.PreludePath != "") _watchPaths.Add(o.PreludePath);
        _testFailurePending = TcsHost.Env("TCS_TEST_RELOAD_PLAN_FAILURE") != "";
    }

    public int Step()
    {
        if (!_started)
        {
            _started = true;
            TcsHost.WriteErr("Watching " + _watchPaths.Count + " file(s)... (Ctrl+C to stop)\n");
            _stamps = Stamps();
            BeginBuild();
            return -1;
        }
        if (_phase == Compiling) StepCompile();
        else if (_phase == Exporting) StepExport();
        else if (_phase == ChunkCompiling) StepChunk();
        else Poll();
        return -1;
    }

    private List<string> Stamps()
    {
        var stamps = new List<string>();
        foreach (var p in _watchPaths) stamps.Add(TcsHost.FileStamp(p));
        return stamps;
    }

    private static bool SameStamps(List<string> a, List<string> b)
    {
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    // 変わるまで待つ。変わったら少し待って (編集の書き込みが落ち着くまで) 変換し直す
    private void Poll()
    {
        var now = Stamps();
        if (SameStamps(now, _stamps))
        {
            TcsHost.Sleep(50);
            return;
        }
        TcsHost.Sleep(100);
        _stamps = Stamps();
        BeginBuild();
    }

    private void Log(string message)
    {
        TcsHost.WriteErr("[" + TcsHost.Clock() + "] " + message + "\n");
    }

    private void BeginBuild()
    {
        _phase = Idle;
        string conflict = Cli.OutputPathConflict(_o);
        if (conflict != "")
        {
            Log("Error: " + conflict);
            return;
        }
        // 消えた入力も変換し直しを起こし、見つからない入力として失敗を報告する
        foreach (var p in _watchPaths)
        {
            if (!TcsHost.FileExists(p))
            {
                Log("Error: file not found: " + p);
                return;
            }
        }
        _sources = Cli.ReadAll(_o.InputPaths);
        _refSources = Cli.ReadAll(_o.RefPaths);
        _session = Cli.NewSession(_o, _sources, _refSources);
        _phase = Compiling;
    }

    private void StepCompile()
    {
        var session = _session!;
        if (!session.Step()) return;
        _phase = Idle;
        var result = session.Result;
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var d in result.Diagnostics)
        {
            if (Cli.IsWarning(d)) warnings.Add(d);
            else errors.Add(d);
        }
        if (!result.Ok || errors.Count > 0)
        {
            if (errors.Count == 0) errors.Add("compile failed");
            Log("Build FAILED:");
            foreach (var e in errors) TcsHost.WriteErr("  " + e + "\n");
            return;
        }
        foreach (var w in warnings) TcsHost.WriteErr("  " + w + "\n");
        // 1 回の変換と同じ判定。前の出力と build の番号はそのまま (chunk も送らない)
        if (_o.WarningsAsErrors && warnings.Count > 0)
        {
            Log("Build FAILED: warnings with --warnaserror");
            return;
        }
        var composed = Cli.Compose(result.Lua, _o);
        if (composed == null) return;
        _lastBuild++;
        _build = _lastBuild;
        _lua = composed.Lua;
        _mapOffset = composed.LineOffset;
        _sourceMap = result.SourceMap;
        if (_o.ReloadChunks)
        {
            _lua = "__tcs_build = " + _build + "\n" + _lua;
            _mapOffset++;
        }
        string conflict = Cli.OutputPathConflict(_o);
        if (conflict != "")
        {
            Log("Error: " + conflict);
            return;
        }
        _planned = false;
        _restartReasons = new List<string>();
        _chunk = "";
        _newExport = null;
        if (!_o.ReloadChunks)
        {
            Publish();
            return;
        }
        // v2 の IL の metadata (次の build の差分の元にもなる)
        var export = new Session("");
        export.ExportOnly = true;
        for (int i = 0; i < _o.InputPaths.Count; i++) export.Add(_o.InputPaths[i], _sources[i], 0);
        foreach (var r in _refSources) export.Add("", r, 1);
        export.Add("TinySystem/Linq.cs", TcsHost.ToolFile("lib/TinySystem/Linq.cs"), 2);
        export.Add("TinySystem/ValueTuple.cs", TcsHost.ToolFile("lib/TinySystem/ValueTuple.cs"), 2);
        export.Start();
        _session = export;
        _phase = Exporting;
    }

    private void StepExport()
    {
        var session = _session!;
        if (!session.Step()) return;
        _phase = Idle;
        var result = session.Result;
        var errors = new List<string>();
        foreach (var d in result.Diagnostics)
        {
            if (!Cli.IsWarning(d)) errors.Add(d);
        }
        if (result.Export == null && errors.Count == 0) errors.Add("export failed");
        _newExport = result.Export;
        if (!_hasRunning)
        {
            Publish();
            return;
        }
        _planned = true;
        if (_testFailurePending)
        {
            // 次の計画を 1 回だけこの message で失敗させる (テストの hook。自然に踏む入力が無い)
            _testFailurePending = false;
            _restartReasons = SplitLines(TcsHost.Env("TCS_TEST_RELOAD_PLAN_FAILURE"));
            Publish();
            return;
        }
        // 型の差分は IL から取るので、IL にできないソースでは移行を組めない
        if (errors.Count > 0 || _runningExport == null || _newExport == null)
        {
            // 動いている版の IL が無い (その版の export が失敗していた) ときは、新しい版の診断が空でも理由を書く
            if (errors.Count == 0) errors.Add("the running build has no IL to compare (its export failed)");
            _restartReasons = SplitLines(string.Join("\n", errors));
            Publish();
            return;
        }
        _restartReasons = ReloadRules.RestartReasons(ReloadRules.TypesOf(_runningExport), ReloadRules.TypesOf(_newExport), true);
        if (_restartReasons.Count > 0)
        {
            Publish();
            return;
        }
        // v2 の定義: chunk は同じ名前の global を旧い identity に戻してから method の本文が解決する前提なので、型の table を local に置かない
        var chunk = new Session("");
        chunk.InstanceRegistry = true;
        chunk.CacheTypeLocals = false;
        chunk.TracePrefixes = _o.TracePrefixes;
        for (int i = 0; i < _o.InputPaths.Count; i++) chunk.Add(_o.InputPaths[i], _sources[i], 0);
        foreach (var r in _refSources) chunk.Add("", r, 1);
        chunk.Add("TinySystem/Linq.cs", TcsHost.ToolFile("lib/TinySystem/Linq.cs"), 2);
        chunk.Add("TinySystem/ValueTuple.cs", TcsHost.ToolFile("lib/TinySystem/ValueTuple.cs"), 2);
        chunk.Start();
        _session = chunk;
        _phase = ChunkCompiling;
    }

    private void StepChunk()
    {
        var session = _session!;
        if (!session.Step()) return;
        _phase = Idle;
        var result = session.Result;
        if (!result.Ok)
        {
            var errors = new List<string>();
            foreach (var d in result.Diagnostics)
            {
                if (!Cli.IsWarning(d)) errors.Add(d);
            }
            if (errors.Count == 0) errors.Add("the reload chunk failed to compile");
            _restartReasons = SplitLines(string.Join("\n", errors));
            Publish();
            return;
        }
        _chunk = ReloadChunk.Emit(_runningExport!, _newExport!, result.Lua, _o.TracePrefixes.Count > 0);
        Publish();
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        if (text == "") return lines;
        foreach (var l in text.Replace("\r\n", "\n").Split('\n')) lines.Add(l);
        return lines;
    }

    // 出力を書き、host への message を送る。message を出力より先に書くと、その間につないだ host は古い出力を読んで chunk を取りこぼす
    private void Publish()
    {
        _session = null;
        string failure = TcsHost.WriteFileAtomic(_o.OutputPath, _lua);
        if (failure != "")
        {
            Log("Error: " + failure);
            return;
        }
        if (_o.EmitSourceMap)
        {
            failure = TcsHost.WriteFileAtomic(_o.OutputPath + ".map", SourceMapText.ToJson(_sourceMap, _mapOffset));
            if (failure != "")
            {
                Log("Error: " + failure);
                return;
            }
        }
        Log("Built " + _o.OutputPath);
        // 初回の build は host が出力をそのまま読む。以後は chunk を書いたとき、または restart を求めたとき
        // (host はこの build の出力で起動し直す) に host の VM がこの build に進む
        if (!_hasRunning)
        {
            Advance();
            return;
        }
        if (!_planned) return;
        if (_restartReasons.Count == 0 && _chunk != "")
        {
            WriteHostMessage("reload_chunk", GuardReloadChunk(_chunk, _runningBuild, _build));
            Log("Sent reload chunk " + _runningBuild + " -> " + _build);
            Advance();
            return;
        }
        Advance();
        Log("Restart required (reload chunk not sent; restart the host to run build " + _build + "):");
        foreach (var r in _restartReasons) TcsHost.WriteErr("  " + r + "\n");
        var body = new List<string>();
        foreach (var r in _restartReasons) body.Add(r + "\n");
        WriteHostMessage("restart_required", string.Join("", body));
    }

    private void Advance()
    {
        _runningBuild = _build;
        _runningExport = _newExport;
        _hasRunning = true;
    }

    // host への message は標準出力へ、`@@tcs_<kind> <UTF-8 の byte 数>` の行と本文の組で順に書く。
    // kind は reload_chunk (本文は当てる Lua の chunk) と restart_required (本文は改行で終わる理由の行)
    public static void WriteHostMessage(string kind, string text)
    {
        TcsHost.WriteOut("@@tcs_" + kind + " " + Strings.ByteLength(text) + "\n");
        TcsHost.WriteOut(text);
        TcsHost.FlushOut();
    }

    // build from → to を当てる chunk。VM の build の番号 (出力と chunk が __tcs_build に書く) が from のときだけ当て、
    // 既に to 以降を含む VM では何もしない。それ以外 (届かなかった chunk がある、出力を読み直した) は何も変えずに失敗する
    private static string GuardReloadChunk(string chunk, int from, int to)
    {
        return "-- TinyC# reload chunk: build " + from + " -> " + to + "\n"
            + "if __tcs_build ~= nil and __tcs_build >= " + to + " then return end\n"
            + "assert(__tcs_build == " + from + ", \"reload chunk for build " + from + " -> " + to + ", \"\n"
            + "  .. \"but the VM runs build \" .. tostring(__tcs_build) .. \" (restart the host)\")\n"
            + chunk
            + "__tcs_build = " + to + "\n";
    }
}

