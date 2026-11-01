using System.Collections.Generic;
using TcsSelf;

// セルフホストの compiler の C の build の入口 (Host/tcsc.lua と同じ引数)。
// usage: tcsc <prelude.lua> [-o out.lua] [--entry Class] [--ref file.cs]... [--lib file.cs]... [--no-runtime] files.cs...
// host は Start を 1 回、Step を終わるまで繰り返し呼ぶ。entry から戻るたびに tcs2c の GC の frame の境界になるので、
// 終わった段の object は次の段の間に回収される。session は static field (GC の root) に置いて段をまたいで持つ
public static class TcscMain
{
    private static Session? _session;
    private static string _prelude = "";
    private static string _output = "";
    private static bool _noRuntime;

    public static void Start()
    {
        int n = TcscHost.ArgCount();
        if (n < 1)
        {
            TcscHost.WriteErr("usage: tcsc <prelude.lua> [-o out.lua] [--entry Class] [--ref file.cs]... [--lib file.cs]... [--no-runtime] files.cs...\n");
            TcscHost.SetExitCode(2);
            TcscHost.SetDone();
            return;
        }
        string prelude = TcscHost.Arg(0);
        string output = "";
        string entry = "";
        bool noRuntime = false;
        var paths = new List<string>();
        var texts = new List<string>();
        var refPaths = new List<string>();
        var refTexts = new List<string>();
        var libPaths = new List<string>();
        var libTexts = new List<string>();
        int i = 1;
        while (i < n)
        {
            string a = TcscHost.Arg(i);
            if (a == "-o" && i + 1 < n)
            {
                output = TcscHost.Arg(i + 1);
                i++;
            }
            else if (a == "--entry" && i + 1 < n)
            {
                entry = TcscHost.Arg(i + 1);
                i++;
            }
            else if (a == "--ref" && i + 1 < n)
            {
                refPaths.Add(TcscHost.Arg(i + 1));
                refTexts.Add(TcscHost.ReadFile(TcscHost.Arg(i + 1)));
                i++;
            }
            else if (a == "--lib" && i + 1 < n)
            {
                libPaths.Add(TcscHost.Arg(i + 1));
                libTexts.Add(TcscHost.ReadFile(TcscHost.Arg(i + 1)));
                i++;
            }
            else if (a == "--no-runtime") noRuntime = true;
            else
            {
                paths.Add(a);
                texts.Add(TcscHost.ReadFile(a));
            }
            i++;
        }
        var session = new Session(entry);
        for (int k = 0; k < paths.Count; k++) session.SetSource(paths[k], texts[k]);
        for (int k = 0; k < refPaths.Count; k++) session.SetReference(refPaths[k], refTexts[k]);
        for (int k = 0; k < libPaths.Count; k++) session.SetLibrary(libPaths[k], libTexts[k]);
        session.Start();
        _session = session;
        _prelude = prelude;
        _output = output;
        _noRuntime = noRuntime;
    }

    // compile を 1 段進める。終わったら結果を書き、host に終わりを知らせる
    public static void Step()
    {
        var session = _session;
        if (session == null)
        {
            TcscHost.SetDone();
            return;
        }
        if (!session.Step()) return;
        _session = null;
        TcscHost.SetDone();
        var result = session.Result;
        foreach (var d in result.Diagnostics) TcscHost.WriteErr(d + "\n");
        if (!result.Ok)
        {
            TcscHost.SetExitCode(1);
            return;
        }
        string text = result.Lua;
        if (!_noRuntime) text = TcscHost.ReadFile(_prelude) + text;
        if (_output != "") TcscHost.WriteFile(_output, text);
        else TcscHost.WriteOut(text);
    }
}
