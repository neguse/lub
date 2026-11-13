// lider が使うエディタの core の口 (前の LiteXl.Host と同じ member と意味)。
// 窓ごとに DocView を 1 つ持ち、id (1 から) で指す。
// 行は 1 から、桁は byte の位置で 1 から数える。
// key と文字は、focus のある view にその場で keymap と command を通して入れる。
// その view から開いた CommandView (検索、行へ移動、file を開く) と NagView (確認) は、view を描くときに一緒に描く (下の帯と上の帯)。
// command が開く doc (Ctrl+N、Ctrl+O、Ctrl+P) は窓の view として作り、TakeOpened で lider に渡す。
using System;
using System.Collections.Generic;
using Lub;

namespace LiderEditor;

public static class EditorHost
{
    // text が query を含まないときの Fuzzy の点
    public const int NoMatch = LiderEditor.Fuzzy.NoMatch;

    static int focused;
    // 続けて押した回数を数える、前に押した所
    static int pressView;
    static float pressX;
    static float pressY;
    static float pressAt = -1;
    static int pressClicks;

    // 桁の幅と行の高さ (px) を渡し、初めてならエディタを起こす (2 度目からは寸法だけを変える)。失敗したら error の文 (成功は "")
    public static string Init(float cellW, float lineH)
    {
        DrawList.CellW = cellW;
        DrawList.LineH = lineH;
        EditorCore.Boot();
        return "";
    }

    // file を開いた view の id (1 から)。開けなければ 0
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

    // command (Ctrl+N、Ctrl+O、Ctrl+P) が開いた doc の view の id を、開いた順に 1 つずつ (無ければ 0)
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

    // view の doc の file の絶対 path (名前の無い doc は "")
    public static string Path(int id)
    {
        return EditorCore.ViewOf(id)?.Doc.AbsFilename ?? "";
    }

    // CommandView か NagView が、この view から開いたものか
    static bool OwnedOverlay(DocView view)
    {
        return EditorCore.LastActive == view.Id && (EditorCore.Active == EditorCore.CommandId || EditorCore.Active == EditorCore.NagId);
    }

    // key と文字の入力を、この view に入れる。
    // 変換中の文字は前の view に確定し、前の view の CommandView は閉じる
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

    // SDL の scancode の key を押した / 離した。その場で keymap を通る
    public static void Key(int scancode, bool down)
    {
        KeyNamed(scancode, down, 0);
    }

    // SDL の scancode から key の名前
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

    // plain はその key が修飾 key なしで打つ文字。記号の位置は配列で違うので、keymap はこの文字で引く。
    // ASCII でない配列でも Ctrl+S などが効くよう、ASCII の時だけ使う
    public static void KeyNamed(int scancode, bool down, int plain)
    {
        var name = plain > 32 && plain < 127 ? ((char)plain).ToString().ToLower() : KeyName(scancode);
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

    // 打った文字 (UTF-8)。すぐ前の key が command になったら捨てる (その key が打った文字)。かなの mode なら IME が受ける
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

    // OS の IME の変換中の文字。start と length は文字の数
    public static void Editing(string text, int start, int length)
    {
        if (!EditorCore.Booted)
            return;
        Ime.OnTextEditing(text, start, length);
        EditorCore.DidKeymap = false;
        EditorCore.UpdateViews();
    }

    // 1 frame 分: view を更新する (scroll を cursor に合わせる)
    public static void Step()
    {
        if (EditorCore.Booted)
            EditorCore.UpdateViews();
    }

    // view を w x h px に描いた命令の数。命令は Cmd* で引く (i は 0 から)。
    // この view から開いた CommandView (下の帯)、NagView (上の帯)、IME の候補も描く (どれも行の番号は -1)
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

    // 1 = rect、2 = text、3 = clip (以降の rect と text を切り抜く範囲)
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

    // RGBA を 32 bit に詰めた色 (R が上の 8 bit)
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

    // 命令が描いた本文の行。gutter など行に付かない物は 0、帯と IME の候補は -1
    public static int CmdLine(int i)
    {
        return i >= 0 && i < DrawList.Count ? DrawList.Line[i] : 0;
    }

    // font の id から、元の Lite XL が読んだ file の名前 ("JetBrainsMono-Regular.ttf"、icon は "icons.ttf")
    public static string FontName(int id)
    {
        return Style.FontName(id);
    }

    // 最後の Draw で描いた cursor の数と、i 番目 (0 から) の左上 (view の中の px)。cursor は命令にしない
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

    // cursor の幅 (px) と色 (CmdColor と同じ詰め方)
    public static float CaretWidth()
    {
        return Style.CaretWidth;
    }

    public static int CaretColor()
    {
        return Style.Caret;
    }

    // 前に呼んでから、どこかに描き直す所ができたか
    public static bool TakeRedraw()
    {
        if (!EditorCore.Redraw)
            return false;
        EditorCore.Redraw = false;
        return true;
    }

    // view の見た目 (本文、選択、scroll、focus、帯、IME) が変わるたびに上がる数
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

    // 見えている最初と最後の行 (最後の Draw の大きさと今の scroll で)
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

    // line 行目の上の端の y と、本文の左の端の x (view の中の px)
    public static float LineY(int id, int line)
    {
        var view = EditorCore.ViewOf(id);
        return view != null ? view.LineY(line) : 0;
    }

    // line 行目 (1 から、行の数 + 1 は最後の行の下) の上に、本文でない行を gaps[line] 行ずつ空ける (lider がそこに描く)
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

    // 空ける行が変わるたびに上がる数
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

    // view の (x, y) px で左ボタンを押す。
    // 同じ view の近くで 0.4 秒以内に続けて押すと 2 回目は語、3 回目は行を選ぶ
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

    // 押したまま (x, y) px へ動かす (押した所からそこまでを選ぶ)
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

    // view の (x, y) px を左 click する (cursor を動かす)
    public static void Click(int id, float x, float y)
    {
        Press(id, x, y);
        Release(id, x, y);
    }

    // view の先頭に line 行目 (1 から) が来るように scroll する
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

    // view を wheel の notch で scroll する (正は上へ。小数も可)。
    // 行き先は整数の px にし、端数は次に回す (lider は scroll した分だけ板の texture をずらすので、半端な px だと文字がぼやける)
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

    // 今の縦の scroll (px)
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

    // n 行目 (改行を除く)
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

    // 本文が変わるたびに変わる数 (undo で前の数に戻る)
    public static int ChangeId(int id)
    {
        var doc = DocOf(id);
        return doc != null ? doc.ChangeId() : 0;
    }

    // 保存した回数 (Ctrl+S も、外の変更を写したのも数える)
    public static int SaveCount(int id)
    {
        var doc = DocOf(id);
        return doc != null ? doc.Saves : 0;
    }

    // 本文から何かを消した回数 (undo で戻した挿入も数える。変換中の文字の入れ替えは数えない) と、最後に消した所の左上 (view の中の px)
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

    // cursor の所に打つ (keymap を通さない)
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

    // 今の key をエディタが受けるか (変換中、CommandView、NagView)
    public static bool Modal()
    {
        if (!EditorCore.Booted)
            return false;
        return Ime.Composing || EditorCore.Active == EditorCore.CommandId || EditorCore.Active == EditorCore.NagId;
    }

    // かな漢字変換の辞書を裏で読み始める。jamo が無ければ false
    public static bool ImeDict(string path)
    {
        return Ime.SetDict(path);
    }

    // かなの mode を切り替える (Ctrl+Space と同じ)
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

    // text が query の文字を順に含むかの点 (大きいほど良く、含まなければ NoMatch)。空白は読まない
    public static int Fuzzy(string text, string query)
    {
        return LiderEditor.Fuzzy.Match(text, query, false);
    }

    // 最後の error (エディタの中で起きた error)
    public static string LastError()
    {
        return EditorCore.LastError;
    }

    // 確認の帯を出す (答えは LastAnswer)。answers の最初が「はい」、最後が「いいえ」
    public static void Ask(int id, string title, string message, List<string> answers)
    {
        Focus(id);
        EditorCore.Nag.Show(new NagRequest { Title = title, Message = message, Options = answers, Yes = 1, No = answers.Count });
    }

    // 確認の帯で最後に選んだ答え
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

    // view の file を消すか尋ね、消すと答えたら file を消して id を覚える (名前の無い doc は尋ねない)
    public static void AskDelete(int id)
    {
        var path = DocOf(id)?.AbsFilename;
        if (path != null)
            AskDeletePath(path, NagView.ActionDeleteView, id);
    }

    // 窓の無い file を消すか尋ねる
    public static void AskDeleteFile(string path)
    {
        AskDeletePath(path, NagView.ActionDeleteFile, 0);
    }

    public static void RemoveFile(string path)
    {
        Paths.Remove(path);
    }

    // 消した file の view の id を 1 つずつ (無ければ 0)
    public static int TakeDeleted()
    {
        if (EditorCore.Deleted.Count == 0)
            return 0;
        var id = EditorCore.Deleted[0];
        EditorCore.Deleted.RemoveAt(0);
        return id;
    }

    // 編集していない doc の file が外で変わっていたら、変わった行だけ写す。
    // undo に残し、カーソルは同じ字に留める。保存したのと同じに扱う
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
        // 最後の行の改行は doc が持ち続けるので、残す行が無ければ改行の手前まで入れ替える
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

    // line, col より前の、空白でない byte の数
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

    // 空白でない byte の数が n になる所
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

    // path の本文を、保存していない編集として doc に入れる (undo で今の本文に戻る)。本文が同じか空なら false
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
