// Incremental fuzzy search that looks up files, declarations and commands in one field.
// Commands (CommandTable) are looked up by name and run when confirmed. Starting with ">" lists only commands.
// Selecting a candidate with up/down shows that location in a window (opening one temporarily if there is none). Return confirms, and Escape goes back to where it was before opening.
// On confirm, the location at the time of opening is kept in the Workspace history (Ctrl+- goes back).
using TinySystem.Text;
using System;
using System.Collections.Generic;
using Lub;
using LiderEditor;

namespace LiderCore;

public class SearchItem
{
    // Text to match (the path from Root for files, Outer.Member for declarations)
    public string Text = "";
    public string Path = "";
    // Declaration line (from 0). -1 for files
    public int Line = -1;
    // Location shown on the right (the declaration's file and line)
    public string Where = "";
    // Creates this file on confirm
    public bool Create;
    // An image or sound file (shown on a board, not in a window)
    public bool Asset;
    // A file with unsaved edits
    public bool Dirty;
    // Name of the command run on confirm (CommandTable; empty if not a command)
    public string Command = "";
}

// Declarations of one file and the version when it was read
class ScannedFile
{
    public int Version = -1;
    public int Revision = -1;
    // Version that changed while typing, and the time it was noticed
    public int ChangedRevision = -1;
    public float ChangedAt;
    public List<SearchItem> Items = new List<SearchItem>();
    public List<Declaration> Declarations = new List<Declaration>();
}

// As content, the search board (drawn by SearchView; it has no picture to copy to the display)
public class Search : IContent
{
    // Window showing the search (focus is put on it while shown; set by whoever places it; if none, focus does not move)
    public Window? Window;
    // The non-Code window that had focus before the search was shown
    Window? focusFrom;

    public TextureRef? Texture()
    {
        return null;
    }

    // When the field is shown as a specific prompt (asking for text, or asking to choose a command), a description of what to enter (empty otherwise)
    public string Prompt = "";
    Action<string>? asked;

    // Shows the field as a text prompt. On Return, passes the typed text (trimmed; not called if empty) to done
    public void Ask(Workspace w, string prompt, Action<string> done)
    {
        Open(w);
        Prompt = prompt;
        asked = done;
        Results.Clear();
    }

    Action<string>? picked;

    // Shows the field as a command prompt. The chosen command is not run; its name is passed to done
    public void Pick(Workspace w, string prompt, Action<string> done)
    {
        Open(w, ">");
        Prompt = prompt;
        picked = done;
    }

    public const int MaxResults = 10;

    public bool Shown;
    public string Query = "";
    public int Selected;
    public readonly List<SearchItem> Results = new List<SearchItem>();
    readonly Dictionary<string, ScannedFile> scanned = new Dictionary<string, ScannedFile>();
    // The tab that had focus before the search, and the tab opened temporarily by selection
    Document? origin;
    Document? opened;
    // Cursor and top line of that file before the selected candidate was shown
    Document? moved;
    int movedLine;
    int movedCol;
    int movedFirst;
    bool dirty;
    // When to reload without waiting for typing to finish
    bool forced;
    const float QuietSeconds = 0.5f;
    // Location at the time the search was opened
    Place? from;
    // Whether some files have not been read yet
    bool pending;
    // Word whose definition to jump to (once reading is done and there is exactly one declaration, jumps there directly)
    string? jumpWord;

    // Jumps to the declaration of the word at the cursor. If there is not exactly one, leaves the search open with that word entered
    public void Jump(Workspace w)
    {
        var doc = w.Focus;
        if (doc == null)
            return;
        var word = WordAt(doc.Text(doc.Line), doc.Col);
        Open(w, word);
        if (word != "")
            jumpWord = word;
        Refresh(w);
    }

    // The word at col in line (or just left of it)
    public static string WordAt(string line, int col)
    {
        var start = Math.Min(col, Utf8.Bytes(line));
        if (start == Utf8.Bytes(line) || !IsWordChar(Utf8.At(line, start)))
            start--;
        if (start < 0 || !IsWordChar(Utf8.At(line, start)))
            return "";
        var end = start;
        while (start > 0 && IsWordChar(Utf8.At(line, start - 1)))
            start--;
        while (end + 1 < Utf8.Bytes(line) && IsWordChar(Utf8.At(line, end + 1)))
            end++;
        return Utf8.Sub(line, start, end - start + 1);
    }

    static bool IsWordChar(int c)
    {
        return char.IsLetterOrDigit((char)c) || c == '_';
    }

    // Declarations of the file at path (empty if not read)
    public List<Declaration> DeclarationsIn(string path)
    {
        return scanned.TryGetValue(path, out var file) ? file.Declarations : new List<Declaration>();
    }

    // Name of the method containing line line (from 1) of path ("" if not read or outside a method)
    public string MethodAt(string path, int line)
    {
        var name = "";
        foreach (var d in DeclarationsIn(path))
            if (d.IsCode && d.Line + 1 <= line)
                name = d.Name;
        return name;
    }

    // Declarations of a name (Inner of Outer.Inner also matches). Called every frame, so no string is created per declaration
    public SearchItem? Find(string name)
    {
        var n = Utf8.Bytes(name);
        // Values creates a List of the values, so the dictionary is iterated directly
        foreach (var pair in scanned)
            foreach (var item in pair.Value.Items)
            {
                if (item.Text == name)
                    return item;
                var t = Utf8.Bytes(item.Text);
                if (t > n && Utf8.At(item.Text, t - n - 1) == '.' && item.Text.EndsWith(name))
                    return item;
            }
        return null;
    }

    // Jumps to line line of path (from 0; -1 just opens the file)
    public void GoToPlace(Workspace w, string path, int line)
    {
        origin = w.Focus;
        from = w.Here();
        Preview(w, new SearchItem { Path = path, Line = line });
        Finish(w);
    }

    // Jumps to the declaration of a name (does nothing if there is none)
    public void GoTo(Workspace w, string name)
    {
        forced = true;
        Refresh(w);
        forced = false;
        var item = Find(name);
        if (item == null)
            return;
        origin = w.Focus;
        from = w.Here();
        Preview(w, item);
        Finish(w);
    }

    public void Open(Workspace w, string query = "")
    {
        Shown = true;
        Query = query;
        Prompt = "";
        asked = null;
        picked = null;
        jumpWord = null;
        from = w.Here();
        Selected = 0;
        origin = w.Focus;
        opened = null;
        moved = null;
        dirty = true;
        if (Window != null && w.Focused != Window)
        {
            focusFrom = w.Focused != w.FocusWindow ? w.Focused : null;
            w.FocusTo(Window);
        }
        Refresh(w);
    }

    // Restores focus when the search closes: to the opened Code on confirm, or to the window from before the search on cancel
    void Unfocus(Workspace w, bool committed)
    {
        if (Window == null || w.Focused != Window)
            return;
        w.FocusCode();
        if (!committed && focusFrom != null && w.ScreenOf(focusFrom) != null)
            w.FocusTo(focusFrom);
        focusFrom = null;
    }

    // Called every frame while shown (picks up declarations from files that finished reading and from changed files)
    public void Refresh(Workspace w)
    {
        pending = false;
        foreach (var path in w.Files)
            dirty = Scan(w, path) || dirty;
        if (dirty)
        {
            dirty = false;
            Filter(w);
        }
        if (jumpWord != null && !pending)
            TryJump(w, jumpWord);
    }

    // If exactly one declaration has exactly the name word, jumps there. If there is none or several, leaves the search open
    void TryJump(Workspace w, string word)
    {
        jumpWord = null;
        SearchItem? found = null;
        foreach (var path in w.Files)
            if (scanned.TryGetValue(path, out var file))
                foreach (var item in file.Items)
                    if (item.Text == word || item.Text.EndsWith("." + word))
                    {
                        if (found != null)
                            return;
                        found = item;
                    }
        if (found == null)
            return;
        Preview(w, found);
        Finish(w);
    }

    bool Scan(Workspace w, string path)
    {
        if (!scanned.TryGetValue(path, out var file))
        {
            file = new ScannedFile();
            scanned[path] = file;
        }
        List<string> lines;
        if (w.Docs.TryGetValue(path, out var doc))
        {
            if (file.Revision == doc.Revision)
                return false;
            // While the search is not shown, does not reload until typing finishes (does not wait on the first read)
            if (file.ChangedRevision != doc.Revision)
            {
                file.ChangedRevision = doc.Revision;
                file.ChangedAt = Sys.Seconds();
            }
            if (!Shown && !forced && file.Revision >= 0 && Sys.Seconds() - file.ChangedAt < QuietSeconds)
                return false;
            file.Revision = doc.Revision;
            lines = doc.Lines();
        }
        else
        {
            Io.LoadText(path, out var text, out var version, out var status, out _);
            if (status == Io.Status.Pending)
                pending = true;
            if (status != Io.Status.Ready || text == null || version == file.Version)
                return false;
            file.Version = version;
            lines = TextBuffer.Lines(text);
        }
        file.Items.Clear();
        file.Declarations = Declarations.Scan(lines);
        foreach (var d in file.Declarations)
            file.Items.Add(new SearchItem { Text = d.Name, Path = path, Line = d.Line, Where = w.Relative(path) + ":" + (d.Line + 1) });
        return true;
    }

    // Keeps up to MaxResults, highest score first. If the text is empty, lists only files
    void Filter(Workspace w)
    {
        var scores = new List<int>();
        Results.Clear();
        if (asked != null)
            return;
        // Starting with ">" lists only commands
        if (Query.StartsWith(">"))
        {
            RankCommands(Utf8.Sub(Query, 1).Trim(), scores);
            Selected = Math.Min(Selected, Math.Max(0, Results.Count - 1));
            return;
        }
        foreach (var path in w.Files)
            Rank(new SearchItem { Text = w.Relative(path), Path = path, Dirty = w.Docs.TryGetValue(path, out var doc) && doc.Dirty }, scores);
        foreach (var path in w.AssetFiles)
            Rank(new SearchItem { Text = w.Relative(path), Path = path, Asset = true }, scores);
        if (Query.Trim() != "")
            foreach (var path in w.Files)
                if (scanned.TryGetValue(path, out var file))
                    foreach (var item in file.Items)
                        Rank(item, scores);
        if (Query.Trim() != "")
            RankCommands(Query, scores);
        OfferCreate(w);
        Selected = Math.Min(Selected, Math.Max(0, Results.Count - 1));
    }

    // If the typed text works as a new file name, adds a create candidate at the end
    void OfferCreate(Workspace w)
    {
        var name = Query.Trim();
        if (name.EndsWith(".cs"))
            name = Utf8.Sub(name, 0, Utf8.Bytes(name) - 3);
        if (!IsTypeName(name))
            return;
        var path = w.PathOf(name + ".cs");
        if (w.Files.Contains(path))
            return;
        if (Results.Count == MaxResults)
            Results.RemoveAt(MaxResults - 1);
        Results.Add(new SearchItem { Text = name + ".cs", Path = path, Where = "新しく作る", Create = true });
    }

    static bool IsTypeName(string name)
    {
        if (name == "" || char.IsDigit((char)Utf8.At(name, 0)))
            return false;
        var u = new Utf8Bytes(name);
        for (var i = 0; i < u.Length; i++)
            if (u[i] >= 128 || !IsWordChar(u[i]))
                return false;
        return true;
    }

    // Looks up commands by name. Shows what each does and its assigned keys on the right
    void RankCommands(string query, List<int> scores)
    {
        foreach (var command in CommandTable.All)
        {
            var stroke = Keymap.ShownOf(command.Name);
            Rank(new SearchItem { Text = command.Name, Where = command.Label + (stroke != "" ? "  [" + stroke + "]" : ""), Command = command.Name }, scores, query);
        }
    }

    void Rank(SearchItem item, List<int> scores)
    {
        Rank(item, scores, Query);
    }

    void Rank(SearchItem item, List<int> scores, string query)
    {
        var score = EditorHost.Fuzzy(item.Text, query);
        if (score == EditorHost.NoMatch)
            return;
        var at = Results.Count;
        while (at > 0 && scores[at - 1] < score)
            at--;
        if (at >= MaxResults)
            return;
        if (Results.Count == MaxResults)
        {
            Results.RemoveAt(MaxResults - 1);
            scores.RemoveAt(MaxResults - 1);
        }
        Results.Add(item);
        scores.Add(score);
        for (var i = Results.Count - 1; i > at; i--)
        {
            Results[i] = Results[i - 1];
            scores[i] = scores[i - 1];
        }
        Results[at] = item;
        scores[at] = score;
    }

    public void Type(Workspace w, string text)
    {
        Query += text;
        Selected = 0;
        Filter(w);
    }

    public void Backspace(Workspace w)
    {
        if (Query == "")
            return;
        // No need to delete a whole UTF-8 character (only ASCII can be typed)
        Query = Utf8.Sub(Query, 0, Utf8.Bytes(Query) - 1);
        Selected = 0;
        Filter(w);
    }

    // Selects the candidate one up or down and shows its location
    public void Move(Workspace w, int by)
    {
        if (Results.Count == 0)
            return;
        Selected = (Selected + by + Results.Count) % Results.Count;
        Preview(w, Results[Selected]);
    }

    // Moves to the selected candidate and closes
    public void Commit(Workspace w)
    {
        if (asked != null)
        {
            var done = asked;
            var typed = Query.Trim();
            Cancel(w);
            if (typed != "")
                done(typed);
            return;
        }
        if (Results.Count == 0)
        {
            Cancel(w);
            return;
        }
        var item = Results[Selected];
        if (item.Command != "")
        {
            // Goes back to where it was before the search opened, then runs the command (passes the name instead if a command is being chosen)
            var pick = picked;
            Cancel(w);
            if (pick != null)
                pick(item.Command);
            else
                CommandTable.Run(item.Command);
            return;
        }
        if (item.Create)
        {
            Cancel(w);
            w.Create(item.Path, Utf8.Sub(item.Text, 0, Utf8.Bytes(item.Text) - 3));
            return;
        }
        Preview(w, item);
        Finish(w);
    }

    void Finish(Workspace w)
    {
        w.Remember(from);
        w.Jumps++;
        moved = null;
        opened = null;
        Shown = false;
        Unfocus(w, true);
    }

    public void Cancel(Workspace w)
    {
        Undo(w);
        w.ShownAsset = "";
        Restore(w);
        moved = null;
        opened = null;
        Shown = false;
        Unfocus(w, false);
    }

    // Returns focus to the tab from when it was opened (leaves it as is if that tab is already closed)
    void Restore(Workspace w)
    {
        if (origin != null && w.WindowOf(origin) != null)
            w.Show(origin);
    }

    void Preview(Workspace w, SearchItem item)
    {
        Undo(w);
        w.ShownAsset = item.Asset ? item.Path : "";
        if (item.Create || item.Asset || item.Command != "")
            return;
        var had = w.Docs.TryGetValue(item.Path, out var open) && w.WindowOf(open) != null;
        w.FocusOn(item.Path);
        var doc = w.Focus;
        if (doc == null)
            return;
        opened = had ? null : doc;
        if (item.Line < 0)
            return;
        moved = doc;
        movedLine = doc.Line;
        movedCol = doc.Col;
        movedFirst = EditorHost.FirstLine(doc.Id);
        doc.MoveTo(item.Line, 0);
        EditorHost.ScrollToLine(doc.Id, Math.Max(1, item.Line + 1 - Workspace.ContextLines));
    }

    void Undo(Workspace w)
    {
        if (moved != null)
        {
            moved.MoveTo(movedLine, movedCol);
            EditorHost.ScrollToLine(moved.Id, Math.Max(1, movedFirst));
            moved = null;
        }
        if (opened != null)
        {
            var window = w.WindowOf(opened);
            if (window != null)
                w.CloseTab(window, window.Code.Tabs.IndexOf(opened));
            opened = null;
            Restore(w);
        }
    }
}
