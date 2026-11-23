// Keeps the window order, tabs and focus, window sizes, and each tab's cursor and top line across VM recreation (lub's restart and App.Restart).
// When recreating, lub calls the old VM's OnQuit and then the new VM's OnInit, and all C# values are lost, so this writes them to a file in OnQuit and reads them back in OnInit.
// OnQuit is also called when lub exits. The Sys.Seconds at write time (seconds since the lub process started) is written along with it,
// and if the Sys.Seconds at read time is earlier than that or more than Seconds seconds later (a later lub launch read it), the file is discarded unused.
using System;
using System.Collections.Generic;
using Lub;
using LiderEditor;

namespace LiderCore;

public static class Session
{
    // Seconds allowed between writing and reading (from the old VM's OnQuit to the OnInit of the new VM's game and lider)
    public const float Seconds = 10;

    // View ids and top lines (from 1) of restored tabs. Applied by ScrollTops once the line height is known
    static readonly List<int> topIds = new List<int>();
    static readonly List<int> topLines = new List<int>();

    static string PathIn(Workspace w)
    {
        return w.Root + "/.lub/lider-session.txt";
    }

    // Line 1 is "<Sys.Seconds when written> <index of the focused window>".
    // Per window, "window <shown tab> <width> <height> <scale> <whether the size was set>", followed per tab by "tab <line> <column> <top line> <path from Root>" (line and column from 0, top line from 1)
    public static void Save(Workspace w, float now)
    {
        var lines = new List<string> { now + " " + (w.FocusWindow != null ? w.Windows.IndexOf(w.FocusWindow) : -1) };
        foreach (var window in w.Windows)
        {
            lines.Add("window " + window.Code.Tab + " " + window.WidthPx + " " + window.HeightPx + " " + window.Scale + " " + (window.Sized ? 1 : 0));
            foreach (var doc in window.Code.Tabs)
                lines.Add("tab " + doc.Line + " " + doc.Col + " " + EditorHost.FirstLine(doc.Id) + " " + w.Relative(doc.Path));
        }
        Io.SaveText(PathIn(w), string.Join("\n", lines));
    }

    // If a layout was written before recreation, arranges the windows that way (the file is deleted after reading). true if restored.
    // Tabs that cannot be opened (unnamed docs, deleted files) are skipped, and windows with no openable tab are not created
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
        // Restored windows go on the code screen (placement on other screens is not recorded)
        foreach (var old in w.Windows)
            w.ScreenOf(old)!.Windows.Remove(old);
        w.FocusWindow = null;
        string[]? spec = null;
        var docs = new List<Document>();
        // Index of the window in the written order (it differs from the index in the restored order when windows without openable tabs are skipped)
        var n = -1;
        var focused = 0;
        for (var i = 1; i <= lines.Length; i++)
        {
            var f = i < lines.Length ? lines[i].Split(' ') : new string[] { "window" };
            if (f[0] == "tab" && f.Length >= 5 && f[4] != "")
            {
                // The path may contain spaces
                var rel = f[4];
                for (var k = 5; k < f.Length; k++)
                    rel += " " + f[k];
                // Files outside Root are written with their path as is
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
                var window = new Window(new Code(docs[0]));
                for (var k = 1; k < docs.Count; k++)
                    window.Code.Tabs.Add(docs[k]);
                window.Code.Tab = Math.Min(int.Parse(spec[1]), docs.Count - 1);
                window.Resize(int.Parse(spec[2]), int.Parse(spec[3]));
                window.Scale = float.Parse(spec[4]);
                window.Sized = spec[5] == "1";
                if (n <= focus)
                    focused = w.CodeScreen.Windows.Count;
                w.CodeScreen.Windows.Add(window);
            }
            // The next window's line (a sentinel at the end)
            spec = f;
            docs = new List<Document>();
        }
        if (w.CodeScreen.Windows.Count > 0)
            w.FocusWindow = w.CodeScreen.Windows[focused];
        return true;
    }

    // Scrolls restored tabs to their top lines (once, after the editor's line height is known)
    public static void ScrollTops()
    {
        for (var i = 0; i < topIds.Count; i++)
            EditorHost.ScrollToLine(topIds[i], topLines[i]);
        topIds.Clear();
        topLines.Clear();
    }
}
