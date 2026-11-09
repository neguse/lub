using System.Collections.Generic;
using TcsVm;

namespace TcsSelf;

// playground (web の editor) のための tcs VM の増分 session の入口 (VmSession。編集ごとに全体を compile し、直前の build からの差分を作る)。
// host は要求を TcsHost の引数に並べて Call を呼び、応答を TcsHost.WriteOut で受け取る:
//   vmopen <entry> <参照の数> [<path> <text>]... [<path> <text>]...  → JSON {ok, kind:"module", module, errors, revision}
//   vmupdate <path> <text>                                            → JSON {ok, kind, module, batch, restartReasons, errors, revision}
//   vmmodule                                                          → 最後に通った build の module (base64)
//   vmconfig <export の数> [<C.M か C.*>]... <trace の数> [<path の先頭>]...  → 次の vmopen が entry の他に作る関数と --trace の path ("ok")
//   vmremove <path>                                                   → vmupdate と同じ (file を session から外す)
// kind は module (最初の build)、batch (batch を tvm_apply_batch で当てる)、restart (module を読み直す)、error (最後の build のまま)。
// module と batch は base64。batch の応答は module を持たない (要るときに vmmodule で受け取る)。entry class の static method を全部 export する
// 同梱の library は TcsHost.ToolFile で読む
public static class Playground
{
    public static void Call()
    {
        int n = TcsHost.ArgCount();
        string method = n > 0 ? TcsHost.Arg(0) : "";
        if (method == "vmopen") VmOpen(n);
        else if (method == "vmupdate") VmUpdateFile(n);
        else if (method == "vmmodule") TcsHost.WriteOut(VBytes.Base64(_vmModule));
        else if (method == "vmconfig") VmConfig(n);
        else if (method == "vmremove") VmRemoveFile(n);
        else TcsHost.WriteOut("{\"ok\":false,\"errors\":[" + Json("unknown request: " + method) + "]}");
    }

    private static VmSession? _vm;
    private static List<string> _vmPaths = new List<string>();
    private static List<string> _vmTexts = new List<string>();
    private static int _vmRevision;
    private static List<int> _vmModule = new List<int>();
    private static List<string> _vmExtraExports = new List<string>();
    private static List<string> _vmTraces = new List<string>();

    private static void VmConfig(int n)
    {
        _vmExtraExports = new List<string>();
        _vmTraces = new List<string>();
        int i = 1;
        int exports = i < n ? ToolText.ParseInt(TcsHost.Arg(i)) : 0;
        i++;
        for (int k = 0; k < exports && i < n; k++)
        {
            _vmExtraExports.Add(TcsHost.Arg(i));
            i++;
        }
        int traces = i < n ? ToolText.ParseInt(TcsHost.Arg(i)) : 0;
        i++;
        for (int k = 0; k < traces && i < n; k++)
        {
            _vmTraces.Add(TcsHost.Arg(i));
            i++;
        }
        TcsHost.WriteOut("ok");
    }

    private static void VmRemoveFile(int n)
    {
        if (_vm == null || n < 2)
        {
            TcsHost.WriteOut("{\"ok\":false,\"kind\":\"error\",\"errors\":[" + Json(_vm == null ? "open the VM session first" : "vmremove needs <path>") + "]}");
            return;
        }
        int at = _vmPaths.IndexOf(TcsHost.Arg(1));
        if (at >= 0)
        {
            _vmPaths.RemoveAt(at);
            _vmTexts.RemoveAt(at);
        }
        VmReply(_vm.Update(_vmTexts, _vmPaths));
    }

    private static void VmOpen(int n)
    {
        string entry = n > 1 ? TcsHost.Arg(1) : "";
        int refCount = n > 2 ? ToolText.ParseInt(TcsHost.Arg(2)) : 0;
        var refs = new List<string>();
        int i = 3;
        for (int k = 0; k < refCount && i + 1 < n; k++)
        {
            refs.Add(TcsHost.Arg(i + 1));
            i += 2;
        }
        _vmPaths = new List<string>();
        _vmTexts = new List<string>();
        while (i + 1 < n)
        {
            _vmPaths.Add(TcsHost.Arg(i));
            _vmTexts.Add(TcsHost.Arg(i + 1));
            i += 2;
        }
        var exports = new List<string>();
        exports.Add(entry + ".*");
        foreach (var e in _vmExtraExports) exports.Add(e);
        _vm = new VmSession("", exports, refs);
        _vm.Traces = _vmTraces;
        _vmRevision = 0;
        VmReply(_vm.Update(_vmTexts, _vmPaths));
    }

    private static void VmUpdateFile(int n)
    {
        if (_vm == null || n < 3)
        {
            TcsHost.WriteOut("{\"ok\":false,\"kind\":\"error\",\"errors\":[" + Json(_vm == null ? "open the VM session first" : "vmupdate needs <path> <text>") + "]}");
            return;
        }
        string path = TcsHost.Arg(1);
        int at = _vmPaths.IndexOf(path);
        if (at < 0)
        {
            _vmPaths.Add(path);
            _vmTexts.Add(TcsHost.Arg(2));
        }
        else _vmTexts[at] = TcsHost.Arg(2);
        VmReply(_vm.Update(_vmTexts, _vmPaths));
    }

    private static void VmReply(VmUpdate u)
    {
        bool ok = u.Kind != "error";
        if (ok)
        {
            _vmRevision++;
            _vmModule = u.Module;
        }
        TcsHost.WriteOut("{\"ok\":" + Bool(ok) + ",\"kind\":" + Json(u.Kind) + ",\"module\":" + Json(ok && u.Kind != "batch" ? VBytes.Base64(u.Module) : "")
            + ",\"batch\":" + Json(u.Kind == "batch" ? VBytes.Base64(u.Batch) : "") + ",\"restartReasons\":" + JsonList(u.Reasons)
            + ",\"errors\":" + JsonList(u.Errors) + ",\"revision\":" + _vmRevision + "}");
    }

    private static string Bool(bool b)
    {
        return b ? "true" : "false";
    }

    private static string Json(string s)
    {
        return "\"" + ToolText.JsonEscape(s) + "\"";
    }

    private static string JsonList(List<string> items)
    {
        var parts = new List<string>();
        foreach (var s in items) parts.Add(Json(s));
        return "[" + string.Join(",", parts) + "]";
    }
}
