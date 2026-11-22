// Editor core interface used by lider (same members and meanings as the former LiteXl.Host).
// Holds one DocView per window, referred to by id (from 1).
// Lines count from 1, and columns count from 1 by byte position.
// Keys and characters go into the focused view immediately through the keymap and commands.
// CommandView (find, go to line, open file) and NagView (confirmation) opened from that view are drawn together with the view (the bottom strip and the top strip).
// Docs opened by commands (Ctrl+N, Ctrl+O, Ctrl+P) are created as window views and passed to lider through TakeOpened.
using System;
using System.Collections.Generic;
using Lub;

namespace LiderEditor;

public static class EditorHost
{
    // Fuzzy score when text does not contain query
    public const int NoMatch = LiderEditor.Fuzzy.NoMatch;

    static int focused;
    // The previous press position, used to count consecutive presses
    static int pressView;
    static float pressX;
    static float pressY;
    static float pressAt = -1;
    static int pressClicks;

    // Passes the column width and line height (px) and starts the editor the first time (from the second time on, only changes the dimensions). Returns the error message on failure ("" on success)
    public static string Init(float cellW, float lineH)
    {
        DrawList.CellW = cellW;
        DrawList.LineH = lineH;
        EditorCore.Boot();
        return "";
    }

    // id (from 1) of the view that opened file. 0 if it cannot be opened
    public static int Open(string path)
    {
        if (!EditorCore.Booted)
            return 0;
        return EditorCore.AddView(EditorCore.OpenDoc(path));
    }

    public static void Close(int id)
    {
        var view = EditorCore.ViewOf(id);
        if (view == null)
            return;
        if (id == focused)
            Ime.Commit();
        EditorCore.Views.Remove(id);
        if (EditorCore.Active == id)
            EditorCore.SetActive(0);
        if (id == focused)
            focused = 0;
        foreach (var pair in EditorCore.Views)
            if (pair.Value.Doc == view.Doc)
                return;
        EditorCore.Docs.Remove(view.Doc);
    }

    // ids of the views of docs opened by commands (Ctrl+N, Ctrl+O, Ctrl+P), one at a time in the order opened (0 if none)
    public static int TakeOpened()
    {
        while (EditorCore.Opened.Count > 0)
        {
            var id = EditorCore.Opened[0];
            EditorCore.Opened.RemoveAt(0);
            if (EditorCore.ViewOf(id) != null)
                return id;
        }
        return 0;
    }

    // Absolute path of the file of the view's doc ("" for an unnamed doc)
    public static string Path(int id)
    {
        return EditorCore.ViewOf(id)?.Doc.AbsFilename ?? "";
    }

    // Whether CommandView or NagView was opened from this view
    static bool OwnedOverlay(DocView view)
    {
        return EditorCore.LastActive == view.Id && (EditorCore.Active == EditorCore.CommandId || EditorCore.Active == EditorCore.NagId);
    }

    // Sends key and character input to this view.
    // Commits the composing text to the previous view and closes the previous view's CommandView
    public static void Focus(int id)
    {
        var view = EditorCore.ViewOf(id);
        if (view == null)
            return;
        if (id != focused)
            Ime.Commit();
        focused = id;
        if (EditorCore.Active != id && !OwnedOverlay(view))
            EditorCore.SetActive(id);
    }

    // Sends keys and characters to no view (while focus is on a window that is not Code). Commits the composing text to the previous view
    public static void Blur()
    {
        if (!EditorCore.Booted || focused == 0)
            return;
        Ime.Commit();
        focused = 0;
        var active = EditorCore.ViewOf(EditorCore.Active);
        if (active != null && !OwnedOverlay(active))
            EditorCore.SetActive(0);
    }

    // Name of the key (the name the keymap looks up; null if none)
    public static string? NameOf(int scancode, int plain)
    {
        return plain > 32 && plain < 127 ? ((char)plain).ToString().ToLower() : KeyName(scancode);
    }

    // A key with an SDL scancode was pressed / released. Goes through the keymap immediately
    public static void Key(int scancode, bool down)
    {
        KeyNamed(scancode, down, 0);
    }

    // Key name from an SDL scancode
    static string? KeyName(int scancode)
    {
        if (scancode >= 4 && scancode <= 29)
            return ((char)('a' + scancode - 4)).ToString();
        if (scancode >= 30 && scancode <= 38)
            return (scancode - 29).ToString();
        if (scancode == 39)
            return "0";
        if (scancode >= 58 && scancode <= 69)
            return "f" + (scancode - 57);
        switch (scancode)
        {
            case 40: return "return";
            case 41: return "escape";
            case 42: return "backspace";
            case 43: return "tab";
            case 44: return "space";
            case 45: return "-";
            case 46: return "=";
            case 47: return "[";
            case 48: return "]";
            case 49: return "\\";
            case 51: return ";";
            case 52: return "'";
            case 53: return "`";
            case 54: return ",";
            case 55: return ".";
            case 56: return "/";
            case 57: return "capslock";
            case 73: return "insert";
            case 74: return "home";
            case 75: return "pageup";
            case 76: return "delete";
            case 77: return "end";
            case 78: return "pagedown";
            case 79: return "right";
            case 80: return "left";
            case 81: return "down";
            case 82: return "up";
            case 88: return "keypad enter";
            case 224: return "left ctrl";
            case 225: return "left shift";
            case 226: return "left alt";
            case 227: return "left gui";
            case 228: return "right ctrl";
            case 229: return "right shift";
            case 230: return "right alt";
            case 231: return "right gui";
        }
        return null;
    }

    // plain is the character the key types without modifiers. Symbol positions differ by layout, so the keymap looks up by this character.
    // It is used only when it is ASCII, so that Ctrl+S and the like work even on non-ASCII layouts
    public static void KeyNamed(int scancode, bool down, int plain)
    {
        var name = NameOf(scancode, plain);
        if (name == null || !EditorCore.Booted)
            return;
        if (down)
        {
            if (Ime.Key(name))
            {
                EditorCore.DidKeymap = true;
                EditorCore.Redraw = true;
                return;
            }
            EditorCore.KeyPressed(name);
        }
        else
        {
            Ime.Release(name);
            EditorCore.KeyReleased(name);
        }
    }

    // Typed text (UTF-8). Discarded if the immediately preceding key became a command (the text typed by that key). In kana mode, the IME receives it
    public static void Text(string text)
    {
        if (!EditorCore.Booted)
            return;
        if (EditorCore.DidKeymap)
        {
            EditorCore.DidKeymap = false;
            return;
        }
        var view = EditorCore.ActiveWindowView();
        if (Ime.Text(text, view != null))
        {
            EditorCore.Redraw = true;
            return;
        }
        EditorCore.TextInput(text);
    }

    // Composing text of the OS IME. start and length are in characters
    public static void Editing(string text, int start, int length)
    {
        if (!EditorCore.Booted)
            return;
        Ime.OnTextEditing(text, start, length);
        EditorCore.DidKeymap = false;
        EditorCore.UpdateViews();
    }

    // One frame: updates the view (scrolls to the cursor)
    public static void Step()
    {
        if (EditorCore.Booted)
            EditorCore.UpdateViews();
    }

    // Number of commands that drew the view at w x h px. Commands are read with Cmd* (i from 0).
    // Also draws the CommandView (bottom strip), NagView (top strip), and IME candidates opened from this view (all with line number -1)
    public static int Draw(int id, float w, float h)
    {
        DrawList.Clear(w, h);
        var view = EditorCore.ViewOf(id);
        if (view == null)
            return 0;
        view.PosX = 0;
        view.PosY = 0;
        view.SizeX = w;
        view.SizeY = h;
        EditorCore.RootW = w;
        EditorCore.RootH = h;
        DrawList.SetClip(0, 0, w, h);
        view.Carets.Clear();
        view.Draw();
        DrawList.Overlay = true;
        var command = OwnedOverlay(view) && EditorCore.Active == EditorCore.CommandId;
        if (command)
            EditorCore.Command.Draw(w, h);
        if (OwnedOverlay(view) && EditorCore.Active == EditorCore.NagId)
            EditorCore.Nag.Draw(w);
        if (EditorCore.Active == id)
            Ime.DrawCandidates(w, h);
        if (command)
            EditorCore.Command.DrawSuggestions();
        DrawList.Overlay = false;
        return DrawList.Count;
    }

    // 1 = rect, 2 = text, 3 = clip (the area that clips the following rects and texts)
    public static int CmdKind(int i)
    {
        return i >= 0 && i < DrawList.Count ? DrawList.Kind[i] : 0;
    }

    public static float CmdX(int i)
    {
        return i >= 0 && i < DrawList.Count ? DrawList.X[i] : 0;
    }

    public static float CmdY(int i)
    {
        return i >= 0 && i < DrawList.Count ? DrawList.Y[i] : 0;
    }

    public static float CmdW(int i)
    {
        return i >= 0 && i < DrawList.Count ? DrawList.W[i] : 0;
    }

    public static float CmdH(int i)
    {
        return i >= 0 && i < DrawList.Count ? DrawList.H[i] : 0;
    }

    // Color with RGBA packed into 32 bits (R in the top 8 bits)
    public static int CmdColor(int i)
    {
        return i >= 0 && i < DrawList.Count ? DrawList.Color[i] : 0;
    }

    public static string CmdText(int i)
    {
        return i >= 0 && i < DrawList.Count ? DrawList.Text[i] : "";
    }

    public static int CmdFont(int i)
    {
        return i >= 0 && i < DrawList.Count ? DrawList.Font[i] : 0;
    }

    // Text line drawn by the command. 0 for things not attached to a line such as the gutter, -1 for the strips and IME candidates
    public static int CmdLine(int i)
    {
        return i >= 0 && i < DrawList.Count ? DrawList.Line[i] : 0;
    }

    // From the font id, the name of the file the original Lite XL loaded ("JetBrainsMono-Regular.ttf", "icons.ttf" for icons)
    public static string FontName(int id)
    {
        return Style.FontName(id);
    }

    // Number of cursors drawn by the last Draw, and the top-left of the i-th (from 0) (px within the view). Cursors are not turned into commands
    public static int CaretCount(int id)
    {
        var view = EditorCore.ViewOf(id);
        return view != null ? view.Carets.Count / 2 : 0;
    }

    public static float CaretX(int id, int i)
    {
        var view = EditorCore.ViewOf(id);
        return view != null && i * 2 < view.Carets.Count ? view.Carets[i * 2] : 0;
    }

    public static float CaretY(int id, int i)
    {
        var view = EditorCore.ViewOf(id);
        return view != null && i * 2 + 1 < view.Carets.Count ? view.Carets[i * 2 + 1] : 0;
    }

    // Cursor width (px) and color (packed the same way as CmdColor)
    public static float CaretWidth()
    {
        return Style.CaretWidth;
    }

    public static int CaretColor()
    {
        return Style.Caret;
    }

    // Whether something somewhere needs redrawing since the previous call
    public static bool TakeRedraw()
    {
        if (!EditorCore.Redraw)
            return false;
        EditorCore.Redraw = false;
        return true;
    }

    // A number that goes up every time the view's appearance (text, selection, scroll, focus, strips, IME) changes
    public static int Version(int id)
    {
        var view = EditorCore.ViewOf(id);
        if (view == null)
            return 0;
        var doc = view.Doc;
        var sig = doc.ChangeId() + ";" + string.Join(",", doc.Selections) + ";" + (int)Math.Floor(view.ScrollX) + ";"
            + (int)Math.Floor(view.ScrollY) + ";" + (EditorCore.Active == id ? "a" : "") + ";" + view.GapsVersion + ";" + DocView.LineNumbers + DocView.MinNumberWidth;
        if (OwnedOverlay(view))
        {
            if (EditorCore.Active == EditorCore.CommandId)
            {
                var c = EditorCore.Command;
                sig += ";" + c.Label + c.GetText() + "|" + string.Join(",", c.View.Doc.Selections) + "|" + c.SuggestionIdx + "|" + c.Suggestions.Count;
            }
            else
                sig += ";" + EditorCore.Nag.Signature();
        }
        if (id == focused)
            sig += ";" + Ime.Signature();
        if (sig != view.Signature)
        {
            view.Signature = sig;
            view.Version++;
        }
        return view.Version;
    }

    // First and last visible lines (with the size of the last Draw and the current scroll)
    public static int FirstLine(int id)
    {
        var view = EditorCore.ViewOf(id);
        return view != null ? view.FirstVisibleLine() : 1;
    }

    public static int LastLine(int id)
    {
        var view = EditorCore.ViewOf(id);
        return view != null ? view.LastVisibleLine() : 0;
    }

    // y of the top edge of line line, and x of the left edge of the text (px within the view)
    public static float LineY(int id, int line)
    {
        var view = EditorCore.ViewOf(id);
        return view != null ? view.LineY(line) : 0;
    }

    // Leaves gaps[line] non-text lines empty above line line (from 1; line count + 1 is below the last line) (lider draws there)
    public static void SetGaps(int id, int[] gaps)
    {
        var view = EditorCore.ViewOf(id);
        if (view == null)
            return;
        var before = view.GapsVersion;
        view.SetGaps(gaps);
        if (view.GapsVersion != before)
            EditorCore.Redraw = true;
    }

    // A number that goes up every time the empty lines change
    public static int GapsVersion(int id)
    {
        var view = EditorCore.ViewOf(id);
        return view != null ? view.GapsVersion : 0;
    }

    public static float TextX(int id)
    {
        var view = EditorCore.ViewOf(id);
        return view != null ? view.LineX() : 0;
    }

    public static float LineHeight(int id)
    {
        var view = EditorCore.ViewOf(id);
        return view != null ? view.LineHeight() : 0;
    }

    // Presses the left button at (x, y) px in the view.
    // Pressing again near the same spot in the same view within 0.4 seconds selects a word on the second press and a line on the third
    public static void Press(int id, float x, float y)
    {
        var view = EditorCore.ViewOf(id);
        if (view == null)
            return;
        Focus(id);
        Ime.Commit();
        var now = Sys.Seconds();
        if (pressView == id && now - pressAt < 0.4f && Math.Abs(x - pressX) < 6 && Math.Abs(y - pressY) < 6)
            pressClicks++;
        else
            pressClicks = 1;
        pressView = id;
        pressX = x;
        pressY = y;
        pressAt = now;
        view.OnMouseMoved(x, y);
        if (!view.OnMousePressed(x, y, pressClicks))
            Keymap.OnMousePressed(x, y, pressClicks);
        EditorCore.Redraw = true;
    }

    // Moves to (x, y) px while held (selects from the pressed position to there)
    public static void Drag(int id, float x, float y)
    {
        var view = EditorCore.ViewOf(id);
        if (view == null)
            return;
        view.OnMouseMoved(x, y);
        EditorCore.Redraw = true;
    }

    public static void Release(int id, float x, float y)
    {
        var view = EditorCore.ViewOf(id);
        if (view == null)
            return;
        view.OnMouseReleased();
        EditorCore.Redraw = true;
    }

    // Left-clicks at (x, y) px in the view (moves the cursor)
    public static void Click(int id, float x, float y)
    {
        Press(id, x, y);
        Release(id, x, y);
    }

    // Scrolls so line line (from 1) is at the top of the view
    public static void ScrollToLine(int id, int line)
    {
        var view = EditorCore.ViewOf(id);
        if (view == null)
            return;
        var y = view.Row(line) * view.LineHeight();
        view.ToY = y;
        view.ScrollY = y;
        EditorCore.Redraw = true;
    }

    // Scrolls the view by wheel notches (positive is up; fractions are allowed).
    // The destination is a whole px and the fraction carries over to the next call (lider shifts the panel texture by the scrolled amount, so fractional px would blur the text)
    public static void Scroll(int id, float notches)
    {
        var view = EditorCore.ViewOf(id);
        if (view == null)
            return;
        var px = view.ScrollRest + notches * -Style.WheelScroll;
        var whole = px >= 0 ? (float)Math.Floor(px) : -(float)Math.Floor(-px);
        view.ScrollRest = px - whole;
        view.ToY += whole;
        EditorCore.Redraw = true;
    }

    // Current vertical scroll (px)
    public static float ScrollY(int id)
    {
        var view = EditorCore.ViewOf(id);
        return view != null ? view.ScrollY : 0;
    }

    static Doc? DocOf(int id)
    {
        return EditorCore.ViewOf(id)?.Doc;
    }

    public static int LineCount(int id)
    {
        var doc = DocOf(id);
        return doc != null ? doc.Lines.Count : 0;
    }

    // Line n (excluding the newline)
    public static string Line(int id, int n)
    {
        var doc = DocOf(id);
        if (doc == null || n < 1 || n > doc.Lines.Count)
            return "";
        return doc.Lines[n - 1];
    }

    public static int CursorLine(int id)
    {
        var doc = DocOf(id);
        if (doc == null)
            return 0;
        doc.GetSelection(false, out var line, out _, out _, out _);
        return line;
    }

    public static int CursorCol(int id)
    {
        var doc = DocOf(id);
        if (doc == null)
            return 0;
        doc.GetSelection(false, out _, out var col, out _, out _);
        return col;
    }

    public static string Selection(int id)
    {
        var doc = DocOf(id);
        if (doc == null)
            return "";
        doc.GetSelection(false, out var l1, out var c1, out var l2, out var c2);
        return doc.GetText(l1, c1, l2, c2);
    }

    public static bool Dirty(int id)
    {
        var doc = DocOf(id);
        return doc != null && doc.IsDirty();
    }

    // A number that changes every time the text changes (undo returns it to the earlier number)
    public static int ChangeId(int id)
    {
        var doc = DocOf(id);
        return doc != null ? doc.ChangeId() : 0;
    }

    // Number of saves (counts Ctrl+S and also copying in external changes)
    public static int SaveCount(int id)
    {
        var doc = DocOf(id);
        return doc != null ? doc.Saves : 0;
    }

    // Number of times something was removed from the text (also counts undone insertions; does not count replacing the composing text), and the top-left of the last removed place (px within the view)
    public static int RemovedCount(int id)
    {
        var doc = DocOf(id);
        return doc != null ? doc.Removed : 0;
    }

    public static float RemovedX(int id)
    {
        var view = EditorCore.ViewOf(id);
        if (view == null || view.Doc.Removed == 0)
            return 0;
        return view.ColX(view.Doc.RemovedLine, view.Doc.RemovedCol);
    }

    public static float RemovedY(int id)
    {
        var view = EditorCore.ViewOf(id);
        if (view == null || view.Doc.Removed == 0)
            return 0;
        return view.LineY(view.Doc.RemovedLine);
    }

    public static void SetCursor(int id, int line, int col)
    {
        DocOf(id)?.SetSelection(line, col);
    }

    // Types at the cursor (without going through the keymap)
    public static void Insert(int id, string text)
    {
        DocOf(id)?.TextInput(text);
    }

    public static void Save(int id)
    {
        var doc = DocOf(id);
        if (doc != null && !doc.Save(null, null))
            EditorCore.Error("no filename set to default to");
    }

    // Whether the editor takes the current key (composing, CommandView, NagView)
    public static bool Modal()
    {
        if (!EditorCore.Booted)
            return false;
        return Ime.Composing || EditorCore.Active == EditorCore.CommandId || EditorCore.Active == EditorCore.NagId;
    }

    // Starts loading the kana-kanji conversion dictionary in the background. false if there is no jamo
    public static bool ImeDict(string path)
    {
        return Ime.SetDict(path);
    }

    // Toggles kana mode (same as Ctrl+Space)
    public static void ImeToggle()
    {
        Ime.Toggle();
    }

    public static bool ImeEnabled()
    {
        return Ime.Enabled;
    }

    public static bool ImeComposing()
    {
        return Ime.Composing;
    }

    // Score of whether text contains the characters of query in order (higher is better; NoMatch if it does not contain them). Whitespace is ignored
    public static int Fuzzy(string text, string query)
    {
        return LiderEditor.Fuzzy.Match(text, query, false);
    }

    // The last error (an error that happened inside the editor)
    public static string LastError()
    {
        return EditorCore.LastError;
    }

    // Shows the confirmation strip (the answer is in LastAnswer). The first of answers is "yes", the last is "no"
    public static void Ask(int id, string title, string message, List<string> answers)
    {
        Focus(id);
        EditorCore.Nag.Show(new NagRequest { Title = title, Message = message, Options = answers, Yes = 1, No = answers.Count });
    }

    // The answer last chosen in the confirmation strip
    public static string LastAnswer()
    {
        return EditorCore.Nag.LastAnswer;
    }

    static void AskDeletePath(string path, int action, int view)
    {
        EditorCore.Nag.Show(new NagRequest
        {
            Title = "file を消す",
            Message = path + " を消しますか",
            Options = new List<string> { "消す", "消さない" },
            Yes = 1,
            No = 2,
            Action = action,
            View = view,
            Path = path,
        });
    }

    // Asks whether to delete the view's file; if the answer is yes, deletes the file and remembers the id (does not ask for an unnamed doc)
    public static void AskDelete(int id)
    {
        var path = DocOf(id)?.AbsFilename;
        if (path != null)
            AskDeletePath(path, NagView.ActionDeleteView, id);
    }

    // Asks whether to delete a file that has no window
    public static void AskDeleteFile(string path)
    {
        AskDeletePath(path, NagView.ActionDeleteFile, 0);
    }

    public static void RemoveFile(string path)
    {
        Paths.Remove(path);
    }

    // ids of the views of deleted files, one at a time (0 if none)
    public static int TakeDeleted()
    {
        if (EditorCore.Deleted.Count == 0)
            return 0;
        var id = EditorCore.Deleted[0];
        EditorCore.Deleted.RemoveAt(0);
        return id;
    }

    // If the file of an unedited doc changed on disk, copies in only the changed lines.
    // Keeps it in undo and keeps the cursor on the same character. Treated the same as a save
    public static void FollowDisk(int id)
    {
        var doc = DocOf(id);
        var path = doc?.AbsFilename;
        if (doc == null || path == null || doc.IsDirty())
            return;
        var info = Disk.Stat(path);
        if (info == null)
            return;
        var stamp = info.Modified + ":" + info.Size;
        if (stamp == doc.DiskStamp)
            return;
        doc.DiskStamp = stamp;
        Io.LoadText(path, out var text, out _, out _, out _);
        if (text == null)
            return;
        var lines = Doc.SplitFile(text);
        var old = doc.Lines;
        var first = 1;
        while (first <= old.Count && first <= lines.Count && old[first - 1] == lines[first - 1])
            first++;
        if (first > old.Count && first > lines.Count)
            return;
        var tail = 0;
        while (tail < old.Count - first + 1 && tail < lines.Count - first + 1 && old[old.Count - 1 - tail] == lines[lines.Count - 1 - tail])
            tail++;
        doc.GetSelection(false, out var cl, out var cc, out _, out _);
        var ink = InkBefore(doc, cl, cc);
        var middle = "";
        for (var i = first; i <= lines.Count - tail; i++)
            middle += lines[i - 1] + "\n";
        // The doc keeps the newline of the last line, so if no lines remain, replaces up to just before the newline
        var oldCount = old.Count;
        if (tail > 0)
        {
            if (oldCount - tail >= first)
                doc.Remove(first, 1, oldCount - tail + 1, 1);
            if (middle != "")
                doc.Insert(first, 1, middle);
        }
        else
        {
            doc.Remove(first, 1, oldCount, doc.LineSize(oldCount));
            doc.Insert(first, 1, Str.Sub(middle, 1, -2));
        }
        doc.Clean();
        doc.Saves++;
        InkPosition(doc, ink, out var nl, out var nc);
        doc.SetSelection(nl, nc);
        EditorCore.Redraw = true;
    }

    // Number of non-whitespace bytes before line, col
    static int InkBefore(Doc doc, int line, int col)
    {
        var n = 0;
        for (var i = 1; i <= line && i <= doc.Lines.Count; i++)
        {
            var s = i == line ? Str.Slice(doc.Lines[i - 1], 0, col - 1) : doc.Lines[i - 1];
            var u = new TinySystem.Text.Utf8Bytes(s);
            for (var j = 0; j < u.Length; j++)
                if (!Str.IsSpace(u[j]))
                    n++;
        }
        return n;
    }

    // The position where the number of non-whitespace bytes reaches n
    static void InkPosition(Doc doc, int n, out int line, out int col)
    {
        for (var i = 1; i <= doc.Lines.Count; i++)
        {
            var u = new TinySystem.Text.Utf8Bytes(doc.Lines[i - 1]);
            for (var c = 1; c <= u.Length; c++)
            {
                var ink = !Str.IsSpace(u[c - 1]);
                if (n <= 0 && ink)
                {
                    line = i;
                    col = c;
                    return;
                }
                if (ink)
                    n--;
            }
        }
        line = doc.Lines.Count;
        col = doc.LineSize(line);
    }

    // Puts the text of path into doc as an unsaved edit (undo returns to the current text). false if the text is the same or empty
    public static bool Restore(int id, string path)
    {
        var doc = DocOf(id);
        if (doc == null)
            return false;
        Io.LoadText(path, out var text, out _, out _, out _);
        if (text == null || text == "" || text == doc.GetAllText())
            return false;
        doc.Remove(1, 1, doc.Lines.Count, Str.Huge);
        doc.Insert(1, 1, text);
        EditorCore.Redraw = true;
        return true;
    }
}
