using System.Collections.Generic;
using TcsVm;

namespace Tcs;

// Entry point for tcs VM sessions (TcsSession) that the host holds by name.
// Used to compile different versions of the same program side by side in one process (such as the saved version and the version being edited).
// The host gives the words of a request to Arg one by one, calls Call, and receives the reply as what Call writes with CliProcess.Out:
//   open <name> <entry> <module path> <batch path> <export count> [<C.M or C.*>]... <trace count> [<path prefix>]... <reference count> [<text>]... [<path> <text>]...
//   update <name> <number of files to remove> [<path>]... [<path> <text>]...   (removes files, adds files, replaces text, and compiles everything at once)
//   close <name>
// open reopens the session with the same name. All static methods of the entry class are exported.
// When compilation passes, writes the module (first build and restart) or the batch (the diff applied with tvm_apply_batch) to the path given in open (TcsFiles.WriteBytes).
// For a batch too, first writes the module after applying the batch to the module path (so the version loaded when recreating matches the applied version).
// The reply is a sequence of lines. Line 1 is the kind (module, batch, restart, error), followed by "revision <pass count>", "error <diagnostic>", "reason <restart reason>", "note <change that was applied but should be reported>".
// Newlines inside diagnostics become spaces.
// Bundled libraries are read with TcsFiles.Shipped.
public static class Sessions
{
    private sealed class Open
    {
        public TcsSession Session;
        public string ModulePath;
        public string BatchPath;
        public int Revision;

        public Open(TcsSession session, string modulePath, string batchPath)
        {
            Session = session;
            ModulePath = modulePath;
            BatchPath = batchPath;
        }
    }

    private static Dictionary<string, Open> _open = new Dictionary<string, Open>();

    // The words of the request being given
    private static List<string> _words = new List<string>();

    public static void Arg(string text)
    {
        _words.Add(text);
    }

    public static void Call()
    {
        var words = _words;
        _words = new List<string>();
        Run(words);
    }

    private static void Run(List<string> a)
    {
        int n = a.Count;
        string method = n > 0 ? a[0] : "";
        string name = n > 1 ? a[1] : "";
        if (method == "open") OpenSession(a, name);
        else if (method == "update") Update(a, name);
        else if (method == "close")
        {
            _open.Remove(name);
            CliProcess.Out("closed\n");
        }
        else Fail("unknown request: " + method);
    }

    private static void OpenSession(List<string> a, string name)
    {
        int n = a.Count;
        if (n < 6)
        {
            Fail("open needs <name> <entry> <module path> <batch path> ...");
            return;
        }
        string entry = a[2];
        int i = 5;
        var exports = new List<string>();
        exports.Add(entry + ".*");
        i = ReadList(a, i, exports);
        var traces = new List<string>();
        i = ReadList(a, i, traces);
        var refs = new List<string>();
        i = ReadList(a, i, refs);
        // The entry class has no Main, so pass it as exports rather than as the VM entry point.
        var session = new TcsSession();
        session.Exports = exports;
        session.Traces = traces;
        foreach (var r in refs) session.Reference(r);
        var s = new Open(session, a[3], a[4]);
        while (i + 1 < n)
        {
            session.Source(a[i], a[i + 1]);
            i += 2;
        }
        _open[name] = s;
        Build(s);
    }

    // Adds a count and that many arguments to the list, and returns the next position.
    private static int ReadList(List<string> a, int i, List<string> list)
    {
        int n = a.Count;
        int count = i < n ? ToolText.ParseInt(a[i]) : 0;
        i++;
        for (int k = 0; k < count && i < n; k++)
        {
            list.Add(a[i]);
            i++;
        }
        return i;
    }

    private static void Update(List<string> a, string name)
    {
        int n = a.Count;
        if (!_open.ContainsKey(name) || n < 3)
        {
            Fail(!_open.ContainsKey(name) ? "no session named " + name : "update needs <name> <removed count> ...");
            return;
        }
        var s = _open[name];
        var removed = new List<string>();
        int i = ReadList(a, 2, removed);
        foreach (var path in removed) s.Session.Remove(path);
        while (i + 1 < n)
        {
            s.Session.Source(a[i], a[i + 1]);
            i += 2;
        }
        Build(s);
    }

    private static void Build(Open s)
    {
        s.Session.Start();
        while (true)
        {
            var r = s.Session.Step();
            if (r == null) continue;
            Reply(s, r);
            return;
        }
    }

    private static void Reply(Open s, TcsResult u)
    {
        var lines = new List<string>();
        if (!u.Failed)
        {
            string failure = TcsFiles.WriteBytes(s.ModulePath, VBytes.ToByteString(u.Module));
            if (failure == "" && u.Kind == "batch") failure = TcsFiles.WriteBytes(s.BatchPath, VBytes.ToByteString(u.Batch));
            if (failure != "")
            {
                Fail(failure);
                return;
            }
            s.Revision++;
        }
        lines.Add(u.Kind);
        lines.Add("revision " + s.Revision);
        foreach (var e in u.ErrorLines()) lines.Add("error " + OneLine(e));
        foreach (var r in u.Reasons) lines.Add("reason " + OneLine(r));
        foreach (var t in u.Notes) lines.Add("note " + OneLine(t));
        CliProcess.Out(string.Join("\n", lines) + "\n");
    }

    private static void Fail(string message)
    {
        CliProcess.Out("error\nrevision 0\nerror " + OneLine(message) + "\n");
    }

    private static string OneLine(string s)
    {
        return s.Replace("\r", "").Replace("\n", " ");
    }
}
