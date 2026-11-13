using System.Collections.Generic;
using TcsVm;

namespace TcsSelf;

// host が名前で持つ、tcs VM の増分 session (VmSession) の入口。
// 同じ process の中で、別の版の同じ program (保存した版と編集中の版など) を並べて変換するのに使う。
// host は要求を TcsHost の引数に並べて Call を呼び、応答を TcsHost.WriteOut で受け取る:
//   open <名前> <entry> <module の path> <batch の path> <export の数> [<C.M か C.*>]... <trace の数> [<path の先頭>]... <参照の数> [<text>]... [<path> <text>]...
//   update <名前> <外す file の数> [<path>]... [<path> <text>]...   (file を外し、足し、本文を替えて、まとめて 1 回変換する)
//   close <名前>
// open は同じ名前の session を開き直す。entry class の static method は全部 export する。
// 変換が通ると、module (最初の build と restart) か batch (tvm_apply_batch で当てる差分) を、open で指した path に書く (TcsHost.WriteBinaryAtomic)。
// batch のときも、batch を当てた後の module を module の path に先に書く (作り直すときに読む版を、当てた版に揃える)。
// 応答は行の並び。1 行目は kind (module、batch、restart、error)、続けて "revision <通った回数>"、"error <診断>"、"reason <restart の理由>"、"note <当てたが知らせたい変化>"。
// 診断の中の改行は空白にする。
// 同梱の library は TcsHost.ToolFile で読む
public static class Sessions
{
    private sealed class Open
    {
        public VmSession Vm;
        public string ModulePath;
        public string BatchPath;
        public List<string> Paths = new List<string>();
        public List<string> Texts = new List<string>();
        public int Revision;

        public Open(VmSession vm, string modulePath, string batchPath)
        {
            Vm = vm;
            ModulePath = modulePath;
            BatchPath = batchPath;
        }
    }

    private static Dictionary<string, Open> _open = new Dictionary<string, Open>();

    public static void Call()
    {
        int n = TcsHost.ArgCount();
        string method = n > 0 ? TcsHost.Arg(0) : "";
        string name = n > 1 ? TcsHost.Arg(1) : "";
        if (method == "open") OpenSession(n, name);
        else if (method == "update") Update(n, name);
        else if (method == "close")
        {
            _open.Remove(name);
            TcsHost.WriteOut("closed\n");
        }
        else Fail("unknown request: " + method);
    }

    private static void OpenSession(int n, string name)
    {
        if (n < 6)
        {
            Fail("open needs <name> <entry> <module path> <batch path> ...");
            return;
        }
        string entry = TcsHost.Arg(2);
        int i = 5;
        var exports = new List<string>();
        exports.Add(entry + ".*");
        i = ReadList(i, n, exports);
        var traces = new List<string>();
        i = ReadList(i, n, traces);
        var refs = new List<string>();
        i = ReadList(i, n, refs);
        // entry class は Main を持たないので、VM の入口でなく export として渡す
        var vm = new VmSession("", exports, refs);
        vm.Traces = traces;
        var s = new Open(vm, TcsHost.Arg(3), TcsHost.Arg(4));
        while (i + 1 < n)
        {
            s.Paths.Add(TcsHost.Arg(i));
            s.Texts.Add(TcsHost.Arg(i + 1));
            i += 2;
        }
        _open[name] = s;
        Reply(s, vm.Update(s.Texts, s.Paths));
    }

    // 数と、その数の引数を list に足し、次の位置を返す
    private static int ReadList(int i, int n, List<string> list)
    {
        int count = i < n ? ToolText.ParseInt(TcsHost.Arg(i)) : 0;
        i++;
        for (int k = 0; k < count && i < n; k++)
        {
            list.Add(TcsHost.Arg(i));
            i++;
        }
        return i;
    }

    private static void Update(int n, string name)
    {
        if (!_open.ContainsKey(name) || n < 3)
        {
            Fail(!_open.ContainsKey(name) ? "no session named " + name : "update needs <name> <removed count> ...");
            return;
        }
        var s = _open[name];
        var removed = new List<string>();
        int i = ReadList(2, n, removed);
        foreach (var path in removed)
        {
            int at = s.Paths.IndexOf(path);
            if (at < 0) continue;
            s.Paths.RemoveAt(at);
            s.Texts.RemoveAt(at);
        }
        while (i + 1 < n)
        {
            string path = TcsHost.Arg(i);
            int at = s.Paths.IndexOf(path);
            if (at < 0)
            {
                s.Paths.Add(path);
                s.Texts.Add(TcsHost.Arg(i + 1));
            }
            else s.Texts[at] = TcsHost.Arg(i + 1);
            i += 2;
        }
        Reply(s, s.Vm.Update(s.Texts, s.Paths));
    }

    private static void Reply(Open s, VmUpdate u)
    {
        var lines = new List<string>();
        if (u.Kind != "error")
        {
            string failure = TcsHost.WriteBinaryAtomic(s.ModulePath, VBytes.ToByteString(u.Module));
            if (failure == "" && u.Kind == "batch") failure = TcsHost.WriteBinaryAtomic(s.BatchPath, VBytes.ToByteString(u.Batch));
            if (failure != "")
            {
                Fail(failure);
                return;
            }
            s.Revision++;
        }
        lines.Add(u.Kind);
        lines.Add("revision " + s.Revision);
        foreach (var e in u.Errors) lines.Add("error " + OneLine(e));
        foreach (var r in u.Reasons) lines.Add("reason " + OneLine(r));
        foreach (var t in u.Notes) lines.Add("note " + OneLine(t));
        TcsHost.WriteOut(string.Join("\n", lines) + "\n");
    }

    private static void Fail(string message)
    {
        TcsHost.WriteOut("error\nrevision 0\nerror " + OneLine(message) + "\n");
    }

    private static string OneLine(string s)
    {
        return s.Replace("\r", "").Replace("\n", " ");
    }
}
