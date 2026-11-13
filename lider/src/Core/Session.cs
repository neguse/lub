// 窓の並びとタブと focus、窓の大きさ、タブごとのカーソルと先頭の行を、VM の作り直し (lub の restart と App.Restart) を跨いで残す。
// lub は作り直すとき古い VM の OnQuit を呼んでから新しい VM の OnInit を呼び、C# の値は全部消えるので、OnQuit で file に書き、OnInit で読み戻す。
// OnQuit は lub を終えるときにも呼ばれる。書いた時の Sys.Seconds (lub の process が始まってからの秒) を一緒に書き、
// 読んだ時の Sys.Seconds がそれより前か、Seconds 秒より後なら (次に起動した lub が読んだ)、使わずに捨てる。
using System;
using System.Collections.Generic;
using Lub;
using LiderEditor;

namespace LiderCore;

public static class Session
{
    // 書いてから読むまで (古い VM の OnQuit から、新しい VM のゲームと lider の OnInit まで) に許す秒
    public const float Seconds = 10;

    // 戻したタブの view の id と先頭の行 (1 から)。行の高さが決まってから ScrollTops で当てる
    static readonly List<int> topIds = new List<int>();
    static readonly List<int> topLines = new List<int>();

    static string PathIn(Workspace w)
    {
        return w.Root + "/.lub/lider-session.txt";
    }

    // 1 行目は「書いた時の Sys.Seconds focus のある窓の番号」。
    // 窓ごとに「window 出しているタブ 幅 高さ 倍率 大きさを決めたか」、続けてタブごとに「tab 行 桁 先頭の行 Root からの path」(行と桁は 0 から、先頭の行は 1 から)
    public static void Save(Workspace w, float now)
    {
        var lines = new List<string> { now + " " + (w.FocusWindow != null ? w.Windows.IndexOf(w.FocusWindow) : -1) };
        foreach (var window in w.Windows)
        {
            lines.Add("window " + window.Tab + " " + window.WidthPx + " " + window.HeightPx + " " + window.Scale + " " + (window.Sized ? 1 : 0));
            foreach (var doc in window.Tabs)
                lines.Add("tab " + doc.Line + " " + doc.Col + " " + EditorHost.FirstLine(doc.Id) + " " + w.Relative(doc.Path));
        }
        Io.SaveText(PathIn(w), string.Join("\n", lines));
    }

    // 作り直す前に書いた並びがあれば、窓をその並びにする (file は読んだら消す)。戻したら true。
    // 開けないタブ (名前の無い doc、消えた file) は飛ばし、タブが 1 つも開けない窓は作らない
    public static bool Restore(Workspace w, float now)
    {
        var path = PathIn(w);
        Io.LoadText(path, out var text, out _, out _, out _);
        if (text == null || text == "")
            return false;
        EditorHost.RemoveFile(path);
        var lines = text.Split('\n');
        var head = lines[0].Split(' ');
        if (head.Length != 2 || !float.TryParse(head[0], out var at) || !int.TryParse(head[1], out var focus) || now < at || now - at > Seconds)
            return false;
        w.Windows.Clear();
        w.FocusWindow = null;
        string[]? spec = null;
        var docs = new List<Document>();
        // 書いた並びの何番目の窓か (タブの開けない窓を飛ばすと、戻した並びの番号とずれる)
        var n = -1;
        var focused = 0;
        for (var i = 1; i <= lines.Length; i++)
        {
            var f = i < lines.Length ? lines[i].Split(' ') : new string[] { "window" };
            if (f[0] == "tab" && f.Length >= 5 && f[4] != "")
            {
                // path は空白を含むことがある
                var rel = f[4];
                for (var k = 5; k < f.Length; k++)
                    rel += " " + f[k];
                // Root の外の file は path のまま書いてある
                var doc = w.Open(rel.StartsWith("/") ? rel : w.PathOf(rel));
                if (doc == null)
                    continue;
                doc.MoveTo(int.Parse(f[1]), int.Parse(f[2]));
                topIds.Add(doc.Id);
                topLines.Add(int.Parse(f[3]));
                docs.Add(doc);
            }
            if (f[0] != "window")
                continue;
            if (spec != null)
                n++;
            if (spec != null && docs.Count > 0)
            {
                var window = new CodeWindow(docs[0]);
                for (var k = 1; k < docs.Count; k++)
                    window.Tabs.Add(docs[k]);
                window.Tab = Math.Min(int.Parse(spec[1]), docs.Count - 1);
                window.Resize(int.Parse(spec[2]), int.Parse(spec[3]));
                window.Scale = float.Parse(spec[4]);
                window.Sized = spec[5] == "1";
                if (n <= focus)
                    focused = w.Windows.Count;
                w.Windows.Add(window);
            }
            // 次の窓の行 (最後は番兵)
            spec = f;
            docs = new List<Document>();
        }
        if (w.Windows.Count > 0)
            w.FocusWindow = w.Windows[focused];
        return true;
    }

    // 戻したタブの先頭の行に scroll する (エディタの行の高さが決まってから、1 度だけ)
    public static void ScrollTops()
    {
        for (var i = 0; i < topIds.Count; i++)
            EditorHost.ScrollToLine(topIds[i], topLines[i]);
        topIds.Clear();
        topLines.Clear();
    }
}
