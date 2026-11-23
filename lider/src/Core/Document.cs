// One open file. The text, cursor and editing are held by the editor core's view (EditorHost, Id), and this exposes what lider looks at.
// lider holds the version the game is running (LiveLines) and the version that has been saved but not yet applied by a reload chunk.
// Saves, whether by the editor's Ctrl+S or by Save, are picked up by Sync from changes in the editor's save count.
using System.Collections.Generic;
using Lub;
using LiderEditor;

namespace LiderCore;

public class Document
{
    public string Path;
    // Id of the editor view
    public readonly int Id;
    // The version the game is running: the last loaded content, or the content applied by a reload chunk after a save (shown in the back layer as the old version of changed lines)
    public List<string> LiveLines;
    // Content that has been saved but not yet applied by a reload chunk
    List<string>? saved;
    int saves;
    int changeId;
    // Incremented whenever the text or LiveLines changes (the window boards and search reload on it)
    public int Revision;
    // Number of saves picked up, and number of times saved content entered the game (the reload was applied)
    public int Saved;
    public int Landed;

    Document(string path, int id)
    {
        Path = path;
        Id = id;
        LiveLines = Lines();
        saves = EditorHost.SaveCount(id);
        changeId = EditorHost.ChangeId(id);
    }

    public static bool Booted;

    // Starts the editor core (from the second call on, only changes the column width and line height).
    // Starts loading the kana-kanji conversion dictionary in the background from env LIDER_JAMO_DICT, or data/mozc.dict under LIDER_HOME if that is missing
    public static void Boot(float cellW, float lineH)
    {
        var err = EditorHost.Init(cellW, lineH);
        if (err != "")
            System.Console.WriteLine("editor: " + err);
        if (Booted)
            return;
        Booted = true;
        var dict = System.Environment.GetEnvironmentVariable("LIDER_JAMO_DICT");
        if (dict == null || dict == "")
            dict = (System.Environment.GetEnvironmentVariable("LIDER_HOME") ?? ".") + "/data/mozc.dict";
        EditorHost.ImeDict(dict);
    }

    // The doc for a view (id) the editor opened. The path of an unnamed doc is ""
    public static Document Adopt(string path, int id)
    {
        return new Document(path, id);
    }

    public static Document? Open(string path)
    {
        if (!Booted)
            Boot(8, 16);
        var id = EditorHost.Open(path);
        if (id == 0)
            return null;
        return new Document(path, id);
    }

    public bool Dirty => EditorHost.Dirty(Id);
    // Cursor line and byte column (both from 0)
    public int Line => EditorHost.CursorLine(Id) - 1;
    public int Col => EditorHost.CursorCol(Id) - 1;

    public int LineCount()
    {
        return EditorHost.LineCount(Id);
    }

    // Line number line (from 0, without the newline)
    public string Text(int line)
    {
        return EditorHost.Line(Id, line + 1);
    }

    public List<string> Lines()
    {
        var lines = new List<string>();
        var n = LineCount();
        for (var i = 0; i < n; i++)
            lines.Add(Text(i));
        return lines;
    }

    // Picks up changes to the editor's text and saves (after passing keys, and every frame)
    public void Sync()
    {
        var id = EditorHost.ChangeId(Id);
        if (id != changeId)
        {
            changeId = id;
            Revision++;
        }
        var n = EditorHost.SaveCount(Id);
        if (n != saves)
        {
            saves = n;
            saved = Lines();
            Saved++;
            Revision++;
        }
    }

    public void Save()
    {
        EditorHost.Save(Id);
        Sync();
    }

    // lub applied a reload chunk (OnReload).
    // lub does not report compile or reload failures, so the first OnReload after a save is taken to mean the saved content entered the game
    public void Reloaded()
    {
        Sync();
        if (saved == null)
            return;
        LiveLines = saved;
        saved = null;
        Landed++;
        Revision++;
    }

    // Text line → LiveLines line (-1 for changed and added lines). Rebuilt when the text or LiveLines changes
    int[] live = new int[0];
    bool differs;
    // In the inline form, the running version's unmatched lines to insert (from 0), and the text line above which each is inserted (from 1; line count + 1 is below the last line).
    // gaps[line] is the number of lines inserted above line number line
    readonly List<int> inlineOld = new List<int>();
    readonly List<int> inlineAt = new List<int>();
    int[] gaps = new int[0];
    int liveChange = -1;
    int liveLanded = -1;

    void MatchLive()
    {
        var change = EditorHost.ChangeId(Id);
        if (change == liveChange && Landed == liveLanded)
            return;
        live = LineDiff.Match(LiveLines, Lines());
        liveChange = change;
        liveLanded = Landed;
        // The mapping is increasing, so if the line counts are equal and every line matches, the content is the same
        differs = live.Length != LiveLines.Count;
        foreach (var l in live)
            if (l < 0)
                differs = true;
        // Unmatched running-version lines between matched lines are inserted above the block on the side of the next matched line (below the last line if there is none)
        inlineOld.Clear();
        inlineAt.Clear();
        gaps = new int[live.Length + 2];
        var prevNew = -1;
        var prevOld = -1;
        for (var line = 0; line <= live.Length; line++)
        {
            var o = line < live.Length ? live[line] : LiveLines.Count;
            if (o < 0)
                continue;
            for (var k = prevOld + 1; k < o; k++)
            {
                inlineOld.Add(k);
                inlineAt.Add(prevNew + 2);
                gaps[prevNew + 2]++;
            }
            prevNew = line;
            prevOld = o;
        }
    }

    // For line number line (from 0), its line in the version the game is running (from 0; -1 for changed and added lines).
    // Even when lines are added or deleted, lines with the same content map to the same line of the running version
    public int LiveLine(int line)
    {
        MatchLive();
        return line >= 0 && line < live.Length ? live[line] : -1;
    }

    // LiveLine for every line of the text
    public int[] LiveMap()
    {
        MatchLive();
        return live;
    }

    // In the inline form, the number of lines inserted above line number line (from 1) (passed to EditorHost.SetGaps)
    public int[] InlineGaps()
    {
        MatchLive();
        return gaps;
    }

    // In the inline form, the running version's lines to insert (from 0, top to bottom) and the text line above which each is inserted (from 1)
    public List<int> InlineOld()
    {
        MatchLive();
        return inlineOld;
    }

    public List<int> InlineAt()
    {
        MatchLive();
        return inlineAt;
    }

    // Whether the text differs from the version the game is running (has changed, added or deleted lines)
    public bool Differs()
    {
        MatchLive();
        return differs;
    }

    // Whether the line differs from the version the game is running (changed and added lines)
    public bool LineChanged(int line)
    {
        return LiveLine(line) < 0;
    }

    // Types at the cursor (may contain newlines)
    public void Type(string s)
    {
        EditorHost.Insert(Id, s);
        Sync();
    }

    public void MoveTo(int line, int col)
    {
        EditorHost.SetCursor(Id, line + 1, col + 1);
    }

    // Moves to the first occurrence of text (false if there is none)
    public bool Find(string text)
    {
        var n = LineCount();
        for (var i = 0; i < n; i++)
        {
            var at = Utf8.IndexOf(Text(i), text);
            if (at >= 0)
            {
                MoveTo(i, at);
                return true;
            }
        }
        return false;
    }
}
