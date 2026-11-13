// 開いた file の本文、選択、undo (Lite XL の core/doc/init.lua を写したもの)。
// 行は 1 から、桁は byte の位置で 1 から数える。行は改行を持たず、行の末尾 (改行の前) の桁は長さ + 1。
// 選択は 4 つの数 (line1, col1, line2, col2) の並びで、幾つも持てる。line1 と col1 が cursor の側。
// undo は Lite XL と同じく 0.3 秒の内の編集を 1 つにまとめ、undo の stack の高さを変更の番号にする。
using System;
using System.Collections.Generic;
using Lub;
using TinySystem.Text;

namespace LiderEditor;

// undo の stack の 1 つ (Kind: 0 は選択、1 は挿入、2 は削除)
public class UndoEdit
{
    public int Kind;
    public float Time;
    public int A;
    public int B;
    public int C;
    public int D;
    public string Text = "";
    public List<int> Sel = new List<int>();
}

public class Doc
{
    public const int UndoSelection = 0;
    public const int UndoInsert = 1;
    public const int UndoRemove = 2;
    public const float UndoMergeSeconds = 0.3f;

    public List<string> Lines = new List<string> { "" };
    public List<int> Selections = new List<int> { 1, 1, 1, 1 };
    public int LastSelection = 1;
    public List<UndoEdit> UndoStack = new List<UndoEdit>();
    public List<UndoEdit> RedoStack = new List<UndoEdit>();
    public int CleanChangeId = 1;
    public bool NewFile;
    // project の dir からの path (外なら絶対 path) と、絶対 path。名前の無い doc は null
    public string? Filename;
    public string? AbsFilename;
    public bool Crlf;
    public readonly Highlighter Highlighter;
    // 保存した回数 (外の変更を写したのも数える)
    public int Saves;
    // 本文から何かを消した回数と、最後に消した所
    public int Removed;
    public int RemovedLine = 1;
    public int RemovedCol = 1;
    // 変換中の文字を入れ替える間は、消した回数に数えない
    public bool Quiet;
    // 外の file の更新時刻と大きさ (FollowDisk)
    public string DiskStamp = "";
    // CommandView の入力欄 (改行を入れない)
    public bool SingleLine;

    public Doc()
    {
        Highlighter = new Highlighter(this);
    }

    // file を開いた doc。file が無ければ名前だけの新しい doc
    public static Doc Open(string? filename, string? abs, bool newFile)
    {
        var doc = new Doc();
        doc.NewFile = newFile;
        if (filename != null)
        {
            doc.SetFilename(filename, abs);
            if (!newFile)
                doc.Load(abs ?? filename);
        }
        return doc;
    }

    public void SetFilename(string? filename, string? abs)
    {
        Filename = filename;
        AbsFilename = abs;
        var path = abs ?? filename ?? "";
        Highlighter.SetCSharp(path.EndsWith(".cs") || path.EndsWith(".csx"));
    }

    // file を読む。CRLF の file は行の末尾の CR を除き、保存するときに戻す
    public void Load(string path)
    {
        Io.LoadText(path, out var text, out _, out _, out _);
        Lines = SplitFile(text ?? "");
        Crlf = SplitCrlf;
        Selections = new List<int> { 1, 1, 1, 1 };
        LastSelection = 1;
        UndoStack = new List<UndoEdit>();
        RedoStack = new List<UndoEdit>();
        CleanChangeId = 1;
        Highlighter.Reset();
    }

    // SplitFile が CRLF を見つけたか
    public static bool SplitCrlf;

    // file の中身を行に分ける (最後の改行の後に空の行を作らない)
    public static List<string> SplitFile(string text)
    {
        var crlf = false;
        var lines = new List<string>();
        foreach (var part in text.Split('\n'))
        {
            var line = part;
            if (line.EndsWith("\r"))
            {
                line = Str.Sub(line, 1, -2);
                crlf = true;
            }
            lines.Add(line);
        }
        if (lines.Count > 1 && lines[lines.Count - 1] == "" && text.EndsWith("\n"))
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0)
            lines.Add("");
        SplitCrlf = crlf;
        return lines;
    }

    // 本文の全体 (保存する形。どの行の後にも改行を置く)
    public string Content()
    {
        var nl = Crlf ? "\r\n" : "\n";
        return string.Join(nl, Lines) + nl;
    }

    // filename に保存する (null なら今の名前)。書けなければ false
    public bool Save(string? filename, string? abs)
    {
        if (filename == null)
        {
            if (Filename == null)
                return false;
            filename = Filename;
            abs = AbsFilename;
        }
        Io.SaveText(abs ?? filename, Content());
        SetFilename(filename, abs);
        NewFile = false;
        Clean();
        Saves++;
        return true;
    }

    public bool IsDirty()
    {
        if (NewFile)
        {
            if (Filename != null)
                return true;
            return Lines.Count > 1 || Lines[0] != "";
        }
        return CleanChangeId != ChangeId();
    }

    public void Clean()
    {
        CleanChangeId = ChangeId();
    }

    public int ChangeId()
    {
        return UndoStack.Count + 1;
    }

    // 行の byte の数 (改行を除く)
    public int LineLen(int line)
    {
        return Str.Len(Lines[line - 1]);
    }

    // Lite XL の #doc.lines[line] (改行を含む長さ)
    public int LineSize(int line)
    {
        return LineLen(line) + 1;
    }

    public int SelectionCount()
    {
        return Selections.Count / 4;
    }

    // idx 番目 (1 から) の選択。sort なら前から後ろの順にし、入れ替えたかを swap に返す
    public void GetSelectionIdx(int idx, bool sort, out int line1, out int col1, out int line2, out int col2, out bool swap)
    {
        var b = (idx - 1) * 4;
        line1 = Selections[b];
        col1 = Selections[b + 1];
        line2 = Selections[b + 2];
        col2 = Selections[b + 3];
        swap = false;
        if (sort && (line1 > line2 || (line1 == line2 && col1 > col2)))
        {
            var l = line1;
            var c = col1;
            line1 = line2;
            col1 = col2;
            line2 = l;
            col2 = c;
            swap = true;
        }
    }

    public bool HasSelectionIdx(int idx)
    {
        return idx >= 1 && idx <= SelectionCount();
    }

    // 最後に足した選択 (無ければ最初)
    public void GetSelection(bool sort, out int line1, out int col1, out int line2, out int col2)
    {
        var idx = HasSelectionIdx(LastSelection) ? LastSelection : 1;
        GetSelectionIdx(idx, sort, out var l1, out var c1, out var l2, out var c2, out _);
        line1 = l1;
        col1 = c1;
        line2 = l2;
        col2 = c2;
    }

    public bool HasSelection()
    {
        GetSelection(false, out var l1, out var c1, out var l2, out var c2);
        return l1 != l2 || c1 != c2;
    }

    public string GetSelectionText()
    {
        var parts = new List<string>();
        for (var idx = 1; idx <= SelectionCount(); idx++)
        {
            GetSelectionIdx(idx, true, out var l1, out var c1, out var l2, out var c2, out _);
            if (l1 != l2 || c1 != c2)
            {
                var t = GetText(l1, c1, l2, c2);
                if (t != "")
                    parts.Add(t);
            }
        }
        return string.Join("\n", parts);
    }

    public void SanitizeSelection()
    {
        for (var idx = 1; idx <= SelectionCount(); idx++)
        {
            GetSelectionIdx(idx, false, out var l1, out var c1, out var l2, out var c2, out _);
            SetSelections(idx, l1, c1, l2, c2, false, 4);
        }
    }

    // idx 番目 (1 から) に選択を置く。rm は置き換える数の 4 倍 (0 なら差し込む)
    public void SetSelections(int idx, int line1, int col1, int line2, int col2, bool swap = false, int rm = 4)
    {
        if (swap)
        {
            var l = line1;
            var c = col1;
            line1 = line2;
            col1 = col2;
            line2 = l;
            col2 = c;
        }
        SanitizePosition(line1, col1, out var sl1, out var sc1);
        SanitizePosition(line2, col2, out var sl2, out var sc2);
        Splice(Selections, (idx - 1) * 4, rm, sl1, sc1, sl2, sc2);
    }

    public void SetCursor(int idx, int line, int col)
    {
        SetSelections(idx, line, col, line, col);
    }

    static void Splice(List<int> t, int at, int remove, int a, int b, int c, int d)
    {
        for (var i = 0; i < remove && at < t.Count; i++)
            t.RemoveAt(at);
        while (at > t.Count)
            t.Add(0);
        t.Insert(at, d);
        t.Insert(at, c);
        t.Insert(at, b);
        t.Insert(at, a);
    }

    public void AddSelection(int line1, int col1, int line2, int col2, bool swap = false)
    {
        SortPositions(line1, col1, line2, col2, out var l1, out var c1, out _, out _);
        var target = SelectionCount() + 1;
        for (var idx = 1; idx <= SelectionCount(); idx++)
        {
            GetSelectionIdx(idx, true, out var tl1, out var tc1, out _, out _, out _);
            if (l1 < tl1 || (l1 == tl1 && c1 < tc1))
            {
                target = idx;
                break;
            }
        }
        SetSelections(target, line1, col1, line2, col2, swap, 0);
        LastSelection = target;
    }

    public void RemoveSelection(int idx)
    {
        if (LastSelection >= idx)
            LastSelection--;
        var at = (idx - 1) * 4;
        for (var i = 0; i < 4; i++)
            Selections.RemoveAt(at);
    }

    public void SetSelection(int line1, int col1, int line2, int col2, bool swap = false)
    {
        Selections = new List<int>();
        SetSelections(1, line1, col1, line2, col2, swap);
        LastSelection = 1;
    }

    public void SetSelection(int line, int col)
    {
        SetSelection(line, col, line, col);
    }

    // 同じ所にある cursor を 1 つにする (idx が 0 でなければ、その選択と重なる物だけ)
    public void MergeCursors(int idx = 0)
    {
        var start = idx != 0 ? (idx - 1) * 4 + 1 : Selections.Count - 3;
        var stop = idx != 0 ? (idx - 1) * 4 + 1 : 5;
        for (var i = start; i >= stop; i -= 4)
        {
            for (var j = 1; j <= i - 4; j += 4)
            {
                if (i + 1 > Selections.Count)
                    break;
                if (Selections[i - 1] == Selections[j - 1] && Selections[i] == Selections[j])
                {
                    for (var k = 0; k < 4; k++)
                        Selections.RemoveAt(i - 1);
                    if (LastSelection >= (i + 3) / 4)
                        LastSelection--;
                    break;
                }
            }
        }
    }

    public static void SortPositions(int line1, int col1, int line2, int col2, out int l1, out int c1, out int l2, out int c2)
    {
        if (line1 > line2 || (line1 == line2 && col1 > col2))
        {
            l1 = line2;
            c1 = col2;
            l2 = line1;
            c2 = col1;
            return;
        }
        l1 = line1;
        c1 = col1;
        l2 = line2;
        c2 = col2;
    }

    public void SanitizePosition(int line, int col, out int l, out int c)
    {
        var n = Lines.Count;
        if (line > n)
        {
            l = n;
            c = LineSize(n);
            return;
        }
        if (line < 1)
        {
            l = 1;
            c = 1;
            return;
        }
        l = line;
        c = Math.Max(Math.Min(col, LineSize(line)), 1);
    }

    // (line, col) から offset byte 進んだ所 (行をまたぐ。改行は 1 byte)
    public void OffsetBytes(int line, int col, int offset, out int l, out int c)
    {
        SanitizePosition(line, col, out var sl, out var sc);
        line = sl;
        col = sc + offset;
        while (line > 1 && col < 1)
        {
            line--;
            col += LineSize(line);
        }
        while (line < Lines.Count && col > LineSize(line))
        {
            col -= LineSize(line);
            line++;
        }
        SanitizePosition(line, col, out var rl, out var rc);
        l = rl;
        c = rc;
    }

    // (line, col) を move で動かした所
    public void Translate(Move move, DocView? dv, int line, int col, out int l, out int c)
    {
        LiderEditor.Translate.Run(this, move, dv, line, col);
        l = LiderEditor.Translate.Line;
        c = LiderEditor.Translate.Col;
    }

    public void OffsetLineCol(int line, int col, int lineOffset, int colOffset, out int l, out int c)
    {
        SanitizePosition(line + lineOffset, col + colOffset, out var rl, out var rc);
        l = rl;
        c = rc;
    }

    // (line1, col1) から (line2, col2) の手前まで (行をまたぐと改行を含む)
    public string GetText(int line1, int col1, int line2, int col2)
    {
        SanitizePosition(line1, col1, out var a1, out var b1);
        SanitizePosition(line2, col2, out var a2, out var b2);
        SortPositions(a1, b1, a2, b2, out var l1, out var c1, out var l2, out var c2);
        if (l1 == l2)
            return Str.Slice(Lines[l1 - 1], c1 - 1, c2 - c1);
        var s = Str.From(Lines[l1 - 1], c1 - 1) + "\n";
        for (var i = l1 + 1; i < l2; i++)
            s += Lines[i - 1] + "\n";
        return s + Str.Slice(Lines[l2 - 1], 0, c2 - 1);
    }

    public string GetAllText()
    {
        return GetText(1, 1, Lines.Count, Str.Huge);
    }

    // (line, col) の byte (行の末尾は改行の 10)
    public int GetChar(int line, int col)
    {
        SanitizePosition(line, col, out var l, out var c);
        var s = Lines[l - 1];
        return c > Str.Len(s) ? 10 : Str.Byte(s, c - 1);
    }

    static void PushUndo(List<UndoEdit> stack, float time, int kind, int a, int b, int c, int d, string text, List<int>? sel)
    {
        var e = new UndoEdit { Kind = kind, Time = time, A = a, B = b, C = c, D = d, Text = text };
        if (sel != null)
            e.Sel = new List<int>(sel);
        stack.Add(e);
    }

    // stack の最後の編集を戻し、0.3 秒の内に続く編集も続けて戻す。戻した編集は other に積む
    void PopUndo(List<UndoEdit> stack, List<UndoEdit> other)
    {
        while (stack.Count > 0)
        {
            var cmd = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (cmd.Kind == UndoInsert)
                RawInsert(cmd.A, cmd.B, cmd.Text, other, cmd.Time);
            else if (cmd.Kind == UndoRemove)
            {
                NoteRemoved(cmd.A, cmd.B, cmd.C, cmd.D);
                RawRemove(cmd.A, cmd.B, cmd.C, cmd.D, other, cmd.Time);
            }
            else
            {
                Selections = new List<int>(cmd.Sel);
                SanitizeSelection();
            }
            if (stack.Count == 0 || Math.Abs(cmd.Time - stack[stack.Count - 1].Time) >= UndoMergeSeconds)
                return;
        }
    }

    // text の行の並び ("a\nb" は "a" と "b"、"a\n" は "a" と "")
    static List<string> SplitLines(string text)
    {
        var res = new List<string>();
        foreach (var part in text.Split('\n'))
            res.Add(part);
        return res;
    }

    void RawInsert(int line, int col, string text, List<UndoEdit> undo, float time)
    {
        var lines = SplitLines(text);
        var len = Str.Len(lines[lines.Count - 1]);
        var cur = Lines[line - 1];
        var before = Str.Slice(cur, 0, col - 1);
        var after = Str.From(cur, col - 1);
        lines[0] = before + lines[0];
        lines[lines.Count - 1] = lines[lines.Count - 1] + after;
        Lines.RemoveAt(line - 1);
        for (var i = lines.Count - 1; i >= 0; i--)
            Lines.Insert(line - 1, lines[i]);

        // cursor を挿入の後ろへずらす
        for (var idx = SelectionCount(); idx >= 1; idx--)
        {
            GetSelectionIdx(idx, true, out var cl1, out var cc1, out var cl2, out var cc2, out _);
            if (cl1 < line)
                break;
            var lineAdd = (line < cl1 || col < cc1) ? lines.Count - 1 : 0;
            var colAdd = (line == cl1 && cc1 > col) ? len : 0;
            SetSelections(idx, cl1 + lineAdd, cc1 + colAdd, cl2 + lineAdd, cc2 + colAdd);
        }

        OffsetBytes(line, col, Str.Len(text), out var line2, out var col2);
        PushUndo(undo, time, UndoSelection, 0, 0, 0, 0, "", Selections);
        PushUndo(undo, time, UndoRemove, line, col, line2, col2, "", null);
        Highlighter.InsertNotify(line, lines.Count - 1);
        SanitizeSelection();
    }

    void RawRemove(int line1, int col1, int line2, int col2, List<UndoEdit> undo, float time)
    {
        var text = GetText(line1, col1, line2, col2);
        PushUndo(undo, time, UndoSelection, 0, 0, 0, 0, "", Selections);
        PushUndo(undo, time, UndoInsert, line1, col1, 0, 0, text, null);

        var before = Str.Slice(Lines[line1 - 1], 0, col1 - 1);
        var after = Str.From(Lines[line2 - 1], col2 - 1);
        var lineRemoval = line2 - line1;
        var colRemoval = col2 - col1;
        for (var i = 0; i < lineRemoval; i++)
            Lines.RemoveAt(line1);
        Lines[line1 - 1] = before + after;

        var merge = false;
        for (var idx = SelectionCount(); idx >= 1; idx--)
        {
            GetSelectionIdx(idx, true, out var cl1, out var cc1, out var cl2, out var cc2, out _);
            if (cl2 < line1)
                break;
            var l1 = cl1;
            var c1 = cc1;
            var l2 = cl2;
            var c2 = cc2;
            if (cl1 > line1 || (cl1 == line1 && cc1 > col1))
            {
                if (cl1 > line2)
                    l1 -= lineRemoval;
                else
                {
                    l1 = line1;
                    c1 = (cl1 == line2 && cc1 > col2) ? c1 - colRemoval : col1;
                }
            }
            if (cl2 > line1 || (cl2 == line1 && cc2 > col1))
            {
                if (cl2 > line2)
                    l2 -= lineRemoval;
                else
                {
                    l2 = line1;
                    c2 = (cl2 == line2 && cc2 > col2) ? c2 - colRemoval : col1;
                }
            }
            if (l1 == line1 && c1 == col1)
                merge = true;
            SetSelections(idx, l1, c1, l2, c2);
        }
        if (merge)
            MergeCursors();
        Highlighter.RemoveNotify(line1, lineRemoval);
        SanitizeSelection();
    }

    void NoteRemoved(int line1, int col1, int line2, int col2)
    {
        if (Quiet || (line1 == line2 && col1 == col2))
            return;
        SortPositions(line1, col1, line2, col2, out var l1, out var c1, out _, out _);
        Removed++;
        RemovedLine = l1;
        RemovedCol = c1;
    }

    public void Insert(int line, int col, string text)
    {
        if (SingleLine)
            text = text.Replace("\n", "");
        RedoStack = new List<UndoEdit>();
        if (ChangeId() < CleanChangeId)
            CleanChangeId = -1;
        SanitizePosition(line, col, out var l, out var c);
        RawInsert(l, c, text, UndoStack, Sys.Seconds());
    }

    public void Remove(int line1, int col1, int line2, int col2)
    {
        RedoStack = new List<UndoEdit>();
        SanitizePosition(line1, col1, out var a1, out var b1);
        SanitizePosition(line2, col2, out var a2, out var b2);
        SortPositions(a1, b1, a2, b2, out var l1, out var c1, out var l2, out var c2);
        NoteRemoved(l1, c1, l2, c2);
        RawRemove(l1, c1, l2, c2, UndoStack, Sys.Seconds());
    }

    public void Undo()
    {
        PopUndo(UndoStack, RedoStack);
    }

    public void Redo()
    {
        PopUndo(RedoStack, UndoStack);
    }

    // 選択を text で置き換える (idx が 0 なら全部の選択、後ろから)
    public void TextInput(string text, int idx = 0)
    {
        var from = idx != 0 ? idx : SelectionCount();
        var to = idx != 0 ? idx : 1;
        for (var sidx = from; sidx >= to; sidx--)
        {
            if (!HasSelectionIdx(sidx))
                continue;
            GetSelectionIdx(sidx, true, out var l1, out var c1, out var l2, out var c2, out _);
            if (l1 != l2 || c1 != c2)
                DeleteToCursor(sidx, Move.None, null);
            Insert(l1, c1, text);
            MoveToCursorBytes(sidx, Str.Len(text));
        }
    }

    // 変換中の文字を選択の所に入れ、入れた文字を選ぶ (cursor は前の端)
    public void ImeTextEditing(string text)
    {
        for (var sidx = SelectionCount(); sidx >= 1; sidx--)
        {
            GetSelectionIdx(sidx, true, out var l1, out var c1, out var l2, out var c2, out _);
            if (l1 != l2 || c1 != c2)
                DeleteToCursor(sidx, Move.None, null);
            Insert(l1, c1, text);
            SetSelections(sidx, l1, c1 + Str.Len(text), l1, c1);
        }
    }

    // 選んだ所を消す。何も選んでいなければ move で動いた所まで消す (idx が 0 なら全部の選択)
    public void DeleteToCursor(int idx, Move move, DocView? dv)
    {
        var from = idx != 0 ? idx : 1;
        var to = idx != 0 ? idx : SelectionCount();
        for (var sidx = from; sidx <= to && sidx <= SelectionCount(); sidx++)
        {
            GetSelectionIdx(sidx, true, out var l1, out var c1, out var l2, out var c2, out _);
            if (l1 != l2 || c1 != c2)
                Remove(l1, c1, l2, c2);
            else
            {
                Translate(move, dv, l1, c1, out var tl, out var tc);
                Remove(l1, c1, tl, tc);
                SortPositions(l1, c1, tl, tc, out var sl, out var sc, out _, out _);
                l1 = sl;
                c1 = sc;
            }
            SetCursor(sidx, l1, c1);
        }
        MergeCursors(idx);
    }

    // 選んだ所を消す。何も選んでいなければ (行、桁) をずらした所まで消す
    public void DeleteToCursorLineCol(int idx, int lineOffset, int colOffset)
    {
        var from = idx != 0 ? idx : 1;
        var to = idx != 0 ? idx : SelectionCount();
        for (var sidx = from; sidx <= to && sidx <= SelectionCount(); sidx++)
        {
            GetSelectionIdx(sidx, true, out var l1, out var c1, out var l2, out var c2, out _);
            if (l1 != l2 || c1 != c2)
                Remove(l1, c1, l2, c2);
            else
            {
                OffsetLineCol(l1, c1, lineOffset, colOffset, out var tl, out var tc);
                Remove(l1, c1, tl, tc);
                SortPositions(l1, c1, tl, tc, out var sl, out var sc, out _, out _);
                l1 = sl;
                c1 = sc;
            }
            SetCursor(sidx, l1, c1);
        }
        MergeCursors(idx);
    }

    public void MoveToCursor(int idx, Move move, DocView? dv)
    {
        var from = idx != 0 ? idx : 1;
        var to = idx != 0 ? idx : SelectionCount();
        for (var sidx = from; sidx <= to && sidx <= SelectionCount(); sidx++)
        {
            GetSelectionIdx(sidx, false, out var l, out var c, out _, out _, out _);
            Translate(move, dv, l, c, out var tl, out var tc);
            SetCursor(sidx, tl, tc);
        }
        MergeCursors(idx);
    }

    public void MoveToCursorBytes(int idx, int offset)
    {
        var from = idx != 0 ? idx : 1;
        var to = idx != 0 ? idx : SelectionCount();
        for (var sidx = from; sidx <= to && sidx <= SelectionCount(); sidx++)
        {
            GetSelectionIdx(sidx, false, out var l, out var c, out _, out _, out _);
            OffsetBytes(l, c, offset, out var tl, out var tc);
            SetCursor(sidx, tl, tc);
        }
        MergeCursors(idx);
    }

    public void SelectToCursor(int idx, Move move, DocView? dv)
    {
        var from = idx != 0 ? idx : 1;
        var to = idx != 0 ? idx : SelectionCount();
        for (var sidx = from; sidx <= to && sidx <= SelectionCount(); sidx++)
        {
            GetSelectionIdx(sidx, false, out var l, out var c, out var l2, out var c2, out _);
            Translate(move, dv, l, c, out var tl, out var tc);
            SetSelections(sidx, tl, tc, l2, c2);
        }
        MergeCursors(idx);
    }

    // 字下げ (Lite XL の soft の 4 桁)
    public const int IndentSize = 4;

    public string IndentString()
    {
        return Str.Repeat(" ", IndentSize);
    }

    // 行の先頭の空白の byte の数 (無ければ 0) と、設定の幅に丸めた字下げ
    public static int LineIndent(string line, bool roundUp, out string indent)
    {
        var e = Str.IndentLength(line);
        var soft = Str.Repeat(" ", IndentSize);
        var raw = e > 0 ? Str.Slice(line, 0, e).Replace("\t", soft) : "";
        var rawLen = Str.Len(raw);
        var number = rawLen / (float)IndentSize;
        var n = roundUp ? (int)Math.Ceiling(number) : (int)Math.Floor(number);
        indent = Str.Sub(raw, 1, n * IndentSize);
        return e;
    }

    // 字下げを足すか (unindent なら) 減らし、新しい選択を返す
    public void IndentText(bool unindent, int line1, int col1, int line2, int col2, out int r1, out int rc1, out int r2, out int rc2)
    {
        var text = IndentString();
        var se = Str.IndentLength(Lines[line1 - 1]);
        var inBeginning = col1 == 1 || (se > 0 && col1 <= se + 1);
        var hasSelection = line1 != line2 || col1 != col2;
        if (unindent || hasSelection || inBeginning)
        {
            var l1d = LineSize(line1);
            var l2d = LineSize(line2);
            for (var line = line1; line <= line2; line++)
            {
                if (!hasSelection || LineSize(line) > 1)
                {
                    var e = LineIndent(Lines[line - 1], unindent, out var rnded);
                    Remove(line, 1, line, e + 1);
                    Insert(line, 1, unindent ? Str.Sub(rnded, 1, Str.Len(rnded) - Str.Len(text)) : rnded + text);
                }
            }
            l1d = LineSize(line1) - l1d;
            l2d = LineSize(line2) - l2d;
            if ((unindent || inBeginning) && !hasSelection)
            {
                var start = (se > 0 ? se + 1 : 1) + l1d;
                r1 = line1;
                rc1 = start;
                r2 = line2;
                rc2 = start;
                return;
            }
            r1 = line1;
            rc1 = col1 + l1d;
            r2 = line2;
            rc2 = col2 + l2d;
            return;
        }
        Insert(line1, col1, text);
        r1 = line1;
        rc1 = col1 + Str.Len(text);
        r2 = line1;
        rc2 = col1 + Str.Len(text);
    }

    // 本文の中を探す (Lite XL の core/doc/search.lua の、正規表現を使わない所)。
    // (line, col) から text を探し、見つけた範囲を返す。wrap なら端から回り、reverse なら前へ探す
    public bool Find(int line, int col, string text, bool noCase, bool wrap, bool reverse,
        out int l1, out int c1, out int l2, out int c2)
    {
        SanitizePosition(line, col, out var sl, out var sc);
        line = sl;
        col = sc;
        l1 = 0;
        c1 = 0;
        l2 = 0;
        c2 = 0;
        if (text == "")
            return false;
        if (noCase)
            text = Str.Lower(text);
        var len = Str.Len(text);
        var n = Lines.Count;
        var step = reverse ? -1 : 1;
        for (var l = line; reverse ? l >= 1 : l <= n; l += step)
        {
            var lineText = Lines[l - 1];
            if (noCase)
                lineText = Str.Lower(lineText);
            var s = -1;
            if (reverse)
            {
                // 終わりが col - 1 より前の、最後の一致
                var limit = col < 0 ? Str.Huge : col - 1;
                var at = Str.Find(lineText, text, 0);
                while (at >= 0 && at + len <= limit)
                {
                    s = at;
                    at = Str.Find(lineText, text, at + 1);
                }
            }
            else
                s = Str.Find(lineText, text, col - 1);
            if (s >= 0)
            {
                l1 = l;
                c1 = s + 1;
                l2 = l;
                c2 = s + len + 1;
                return true;
            }
            col = reverse ? -1 : 1;
        }
        if (!wrap)
            return false;
        var wl = reverse ? n : 1;
        var wc = reverse ? LineSize(n) : 1;
        var found = Find(wl, wc, text, noCase, false, reverse, out var a1, out var b1, out var a2, out var b2);
        l1 = a1;
        c1 = b1;
        l2 = a2;
        c2 = b2;
        return found;
    }
}
