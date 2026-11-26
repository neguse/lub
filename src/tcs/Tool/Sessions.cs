using System.Collections.Generic;
using TcsVm;

namespace Tcs;

// Entry point for tcs VM incremental sessions (VmSession) that the host holds by name.
// Used to compile different versions of the same program side by side in one process (such as the saved version and the version being edited).
// The host puts the request in the TcsHost arguments, calls Call, and receives the reply through TcsHost.WriteOut:
//   open <name> <entry> <module path> <batch path> <export count> [<C.M or C.*>]... <trace count> [<path prefix>]... <reference count> [<text>]... [<path> <text>]...
//   update <name> <number of files to remove> [<path>]... [<path> <text>]...   (removes files, adds files, replaces text, and compiles everything at once)
//   close <name>
// open reopens the session with the same name. All static methods of the entry class are exported.
// When compilation passes, writes the module (first build and restart) or the batch (the diff applied with tvm_apply_batch) to the path given in open (TcsHost.WriteBinaryAtomic).
// For a batch too, first writes the module after applying the batch to the module path (so the version loaded when recreating matches the applied version).
// The reply is a sequence of lines. Line 1 is the kind (module, batch, restart, error), followed by "revision <pass count>", "error <diagnostic>", "reason <restart reason>", "note <change that was applied but should be reported>".
// Newlines inside diagnostics become spaces.
// Bundled libraries are read with TcsHost.ToolFile.
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
        // The entry class has no Main, so pass it as exports rather than as the VM entry point.
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

    // Adds a count and that many arguments to the list, and returns the next position.
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
