using System.Collections.Generic;
using TcsC;
using TcsVm;

namespace TcsSelf;

// `tcs vm`: tcs VM の .tcsb を作る (旧 tcs2vm と同じ引数)。IL を SelfHost/VmBackend が .tcsb にする
public sealed class VmOptions
{
    public const string Usage = "usage: tcs vm [--entry CLASS] [--export CLASS.METHOD|CLASS.*] [--ref HOST_STUB.cs] [--watch] [--dump-il] [--reload-from OLD.cs ... --plan OUT.plan | --batch OUT.batch] [-o OUT.tcsb] INPUT.cs ...";
    public List<string> Inputs = new List<string>();
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

/// <summary>VM の増分 session の 1 回の結果。Kind は "module" (最初の build。Module を読み込む)、"batch" (Batch を
/// tvm_apply_batch で当てる)、"restart" (Module を読み直す。Reasons が理由)、"error" (Errors。最後の build のまま)。</summary>
public sealed class VmUpdate
{
    public string Kind;
    public List<int> Module = new List<int>();
    public List<int> Batch = new List<int>();
    public List<string> Reasons = new List<string>();
    public List<string> Errors = new List<string>();

    public VmUpdate(string kind)
    {
        Kind = kind;
    }
}

/// <summary>VM の増分 session (doc/vm-design.md §13.3、V5): 最後に通った build を持ち、編集ごとに reload の batch か
/// restart を返す。`tcs vm --watch` と web の playground の worker が同じものを使う。診断のある編集は最後の build を変えない。</summary>
public sealed class VmSession
{
    private string _entry;
    private List<string> _exports;
    private List<string> _refs;
    private ExportResult? _last;
    // 編集を跨いで持つ compile の session (内容の変わらないファイルは parse し直さない)
    private Session? _session;
    // 最後に通った build の型と static の記述 (次の reload の旧版)
    private VmDescription? _lastDesc;

    public VmSession(string entryClass, List<string> exports, List<string> referenceSources)
    {
        _entry = entryClass;
        _exports = exports;
        _refs = referenceSources;
    }

    /// <summary>入力を compile して VM が受ける IL にする。止める診断があれば null で diagnostics に入れる。</summary>
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
        for (int i = 0; i < sources.Count; i++) _session.SetSource(i < paths.Count && paths[i] != "" ? paths[i] : "#" + i, sources[i]);
        var next = VmEmitter.Exported(_session.Compile(), diags);
        if (next == null)
        {
            var e = new VmUpdate("error");
            e.Errors = diags;
            return e;
        }
        CFail.Error = "";
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
        var m = VmEmitter.EmitReload(_lastDesc!, next, _entry, _exports, RestartReasons(_last, next), out List<int> plan, out List<string> reasons,
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
        return b;
    }

    /// <summary>live に当てられない編集の理由 (Lua の reload と同じ規則)。</summary>
    public static List<string> RestartReasons(ExportResult oldProgram, ExportResult newProgram) =>
        ReloadRules.RestartReasons(ReloadRules.TypesOf(oldProgram), ReloadRules.TypesOf(newProgram), true);

    private static VmUpdate Failed()
    {
        var e = new VmUpdate("error");
        e.Errors.Add(CFail.Error);
        CFail.Error = "";
        return e;
    }
}

public sealed class VmRun
{
    private VmOptions _o;
    private VmSession? _watchSession;
    private string _stamp = "";

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
        TcsHost.WriteErr("tcs2vm: " + message + "\n");
        return 1;
    }

    private int WriteBytes(string path, List<int> bytes)
    {
        string failure = TcsHost.WriteBinaryAtomic(path, VBytes.ToByteString(bytes));
        if (failure != "") return VError(failure);
        return 0;
    }

    // 1 回で終わる仕事は最初の Step で全部行う。--watch は以後の Step で入力の変化を待つ
    public int Step()
    {
        if (_watchSession != null) return StepWatch();
        var sources = ReadAll(_o.Inputs);
        if (sources == null) return VError("could not find file: " + MissingFile(_o.Inputs));
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
        if (_o.ReloadFrom.Count > 0)
        {
            // reload: 旧版 (--reload-from) と新版から、新版の module と計画 (--plan) を作る (doc/object-model.md §6)
            if (_o.PlanOut == "" && _o.BatchOut == "") return VError("--plan or --batch is required with --reload-from");
            var oldSources = ReadAll(_o.ReloadFrom);
            if (oldSources == null) return VError("could not find file: " + MissingFile(_o.ReloadFrom));
            var oldDiags = new List<string>();
            var old = VmSession.Export(oldSources, _o.ReloadFrom, refs, oldDiags);
            if (old == null) return VError("TinyC# diagnostics (old version):\n" + string.Join("\n", oldDiags));
            var module = VmEmitter.EmitReload(old, exported, _o.Entry, _o.Exports, VmSession.RestartReasons(old, exported), out List<int> plan, out List<string> reasons);
            if (CFail.Failed) return VError(CFail.Error);
            if (WriteBytes(_o.Output, module) != 0) return 1;
            if (_o.PlanOut != "" && WriteBytes(_o.PlanOut, plan) != 0) return 1;
            if (_o.BatchOut != "" && WriteBytes(_o.BatchOut, VmEmitter.Batch(module, plan)) != 0) return 1;
            return 0;
        }
        if (_o.Watch) return StartWatch(sources);
        var bytes = VmEmitter.Emit(exported, _o.Entry, _o.Exports);
        if (CFail.Failed) return VError(CFail.Error);
        return WriteBytes(_o.Output, bytes);
    }

    // ---- --watch (doc/vm-design.md §13.3): 入力が変わるたびに直前の build からの reload を作り、-o を書き直してから
    // 標準出力へ `@@tcs_<kind> <byte 数>` の行と本文を書く。kind は vm_batch (本文は batch の base64 の 1 行と改行。
    // host は tvm_apply_batch で当てる) と restart_required (本文は改行終端の理由の行。host は -o を読み直す)。
    // 診断のある編集は stderr に書いて、次の編集を待つ。以後の差分は最後に書いた build から作る ----

    private string Stamp()
    {
        var parts = new List<string>();
        foreach (var p in _o.Inputs) parts.Add(TcsHost.FileStamp(p));
        return string.Join("|", parts);
    }

    private int StartWatch(List<string> sources)
    {
        var refs = ReadAll(_o.Refs);
        var session = new VmSession(_o.Entry, _o.Exports, refs == null ? new List<string>() : refs);
        var first = session.Update(sources, _o.Inputs);
        if (first.Kind != "module") return VError(string.Join("\n", first.Errors));
        if (WriteBytes(_o.Output, first.Module) != 0) return 1;
        _stamp = Stamp();
        TcsHost.WriteErr("tcs2vm: watching\n");
        _watchSession = session;
        return -1;
    }

    private int StepWatch()
    {
        TcsHost.Sleep(100);
        string now = Stamp();
        if (now == _stamp) return -1;
        _stamp = now;
        var sources = ReadAll(_o.Inputs);
        if (sources == null) return -1;
        var r = _watchSession!.Update(sources, _o.Inputs);
        if (r.Kind == "error")
        {
            TcsHost.WriteErr("tcs2vm: " + string.Join("\n", r.Errors) + "\n");
            return -1;
        }
        if (WriteBytes(_o.Output, r.Module) != 0) return -1;
        if (r.Kind == "restart")
        {
            var body = new List<string>();
            foreach (var x in r.Reasons) body.Add(x + "\n");
            WatchRun.WriteHostMessage("restart_required", string.Join("", body));
        }
        else WatchRun.WriteHostMessage("vm_batch", VBytes.Base64(r.Batch) + "\n");
        return -1;
    }

    // ---- --dump-il: IL の診断用テキスト (非規範) ----

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
