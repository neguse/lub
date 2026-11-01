using System.Collections.Generic;

namespace TcsSelf;

// playground (web の editor) のための増分 session の入口 (WasmCompiler の SessionExports と同じ要求と応答)。
// host は要求を TcsHost の引数に並べて Call を呼び、応答を TcsHost.WriteOut で受け取る:
//   open <entry> <参照の数> [<path> <text>]... [<path> <text>]...   → JSON {ok, errors, warnings, epoch, revision}
//   update <epoch> <path> <text>                                      → JSON {ok, fastPath, requiresRestart, restartReasons, errors, epoch, revision, ...}
//   link <epoch>                                                      → bridge snapshot の Lua (失敗は `--@@tcs_error: ` で始まる 1 行)
// runtime と同梱の library は TcsHost.ToolFile で読む
public static class Playground
{
    private static ModuleSession? _session;
    private static int _epoch;
    private static string _entry = "";

    public static void Call()
    {
        int n = TcsHost.ArgCount();
        string method = n > 0 ? TcsHost.Arg(0) : "";
        if (method == "open") Open(n);
        else if (method == "update") Update(n);
        else if (method == "link") Link(n);
        else TcsHost.WriteOut("{\"ok\":false,\"errors\":[" + Json("unknown request: " + method) + "]}");
    }

    private static void Open(int n)
    {
        _epoch++;
        _entry = n > 1 ? TcsHost.Arg(1) : "";
        int refCount = n > 2 ? SourceMapText.ParseInt(TcsHost.Arg(2)) : 0;
        var refs = new List<string>();
        int i = 3;
        for (int k = 0; k < refCount && i + 1 < n; k++)
        {
            refs.Add(TcsHost.Arg(i + 1));
            i += 2;
        }
        var paths = new List<string>();
        var texts = new List<string>();
        while (i + 1 < n)
        {
            paths.Add(TcsHost.Arg(i));
            texts.Add(TcsHost.Arg(i + 1));
            i += 2;
        }
        var session = new ModuleSession(refs);
        session.Open(paths, texts);
        _session = session;
        var errors = new List<string>();
        var warnings = new List<string>();
        if (session.OpenError != "") errors.Add(session.OpenError);
        foreach (var e in session.Errors()) errors.Add(e);
        foreach (var w in session.Warnings()) warnings.Add(w);
        TcsHost.WriteOut("{\"ok\":" + Bool(errors.Count == 0) + ",\"errors\":" + JsonList(errors) + ",\"warnings\":" + JsonList(warnings)
            + ",\"epoch\":" + _epoch + ",\"revision\":" + session.Revision + "}");
    }

    private static void Update(int n)
    {
        var session = _session;
        string failure = "";
        if (session == null) failure = "open the session first";
        else if (n < 4) failure = "update needs <epoch> <path> <text>";
        else if (SourceMapText.ParseInt(TcsHost.Arg(1)) != _epoch) failure = "stale epoch " + TcsHost.Arg(1) + " (current " + _epoch + ")";
        else if (!session.Has(TcsHost.Arg(2))) failure = "unknown module: " + TcsHost.Arg(2);
        if (failure != "" || session == null)
        {
            TcsHost.WriteOut("{\"ok\":false,\"errors\":[" + Json(failure) + "],\"epoch\":" + _epoch + "}");
            return;
        }
        var u = session.Update(TcsHost.Arg(2), TcsHost.Arg(3));
        int bytes = 0;
        foreach (var a in u.ChangedArtifacts) bytes += Strings.ByteLength(a.Lua);
        TcsHost.WriteOut("{\"ok\":" + Bool(u.Success) + ",\"fastPath\":" + Bool(u.FastPath) + ",\"requiresRestart\":" + Bool(u.RequiresRestart)
            + ",\"restartReasons\":" + JsonList(u.RestartReasons) + ",\"errors\":" + JsonList(u.Errors)
            + ",\"epoch\":" + _epoch + ",\"revision\":" + session.Revision
            + ",\"parsedTreeCount\":" + u.ParsedTreeCount + ",\"emittedModuleCount\":" + u.EmittedModuleCount + ",\"artifactBytes\":" + bytes + "}");
    }

    private static void Link(int n)
    {
        var session = _session;
        if (session == null)
        {
            TcsHost.WriteOut("--@@tcs_error: open the session first");
            return;
        }
        if (n < 2 || SourceMapText.ParseInt(TcsHost.Arg(1)) != _epoch)
        {
            TcsHost.WriteOut("--@@tcs_error: stale epoch " + (n > 1 ? TcsHost.Arg(1) : "") + " (current " + _epoch + ")");
            return;
        }
        string tinySystem = TcsHost.ToolFile("runtime/tinysystem.lua");
        string registry = TcsHost.ToolFile("runtime/module_registry.lua");
        if (tinySystem == "" || registry == "")
        {
            TcsHost.WriteOut("--@@tcs_error: TinyC# runtime file not found");
            return;
        }
        var lua = ModuleLinker.LinkSnapshot(session.Artifacts(), session.Revision, _entry, tinySystem, registry, true);
        if (lua == null) TcsHost.WriteOut("--@@tcs_error: " + ModuleLinker.Error.Replace("\n", " "));
        else TcsHost.WriteOut(lua);
    }

    private static string Bool(bool b)
    {
        return b ? "true" : "false";
    }

    private static string Json(string s)
    {
        return "\"" + SourceMapText.JsonEscape(s) + "\"";
    }

    private static string JsonList(List<string> items)
    {
        var parts = new List<string>();
        foreach (var s in items) parts.Add(Json(s));
        return "[" + string.Join(",", parts) + "]";
    }
}
