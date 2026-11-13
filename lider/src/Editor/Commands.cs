// 編集の command (Lite XL の core/commands/doc.lua、findreplace.lua、command.lua、dialog.lua、core.lua の、lider が使う所)。
// command は名前で呼び、その時の active の view で効くか (predicate) を見る。効いたら true。
using System;
using System.Collections.Generic;

namespace LiderEditor;

public static class Commands
{
    // 検索の状態 (findreplace.lua の last_view、last_sel、last_text)
    static int lastFindView;
    static List<int> lastSel = new List<int>();
    static string lastText = "";
    static bool hasLastFind;
    static bool foundExpression;
    static bool caseSensitive;

    public static bool Perform(string name, float x = 0, float y = 0, int clicks = 0)
    {
        var dv = EditorCore.ActiveDocView();
        var window = EditorCore.ActiveWindowView();
        var command = EditorCore.Active == EditorCore.CommandId;
        var nag = EditorCore.Active == EditorCore.NagId;

        if (name.StartsWith("doc:move-to-") || name.StartsWith("doc:select-to-") || name.StartsWith("doc:delete-to-"))
        {
            if (dv == null)
                return false;
            var prefix = name.StartsWith("doc:move-to-") ? "doc:move-to-" : name.StartsWith("doc:select-to-") ? "doc:select-to-" : "doc:delete-to-";
            var move = Translate.Parse(Str.From(name, Str.Len(prefix)));
            if (name == "doc:select-to-cursor")
                return SelectToCursor(dv, x, y);
            if (move == Move.None)
                return false;
            if (name == "doc:move-to-previous-char")
                MoveToPreviousChar(dv);
            else if (name == "doc:move-to-next-char")
                MoveToNextChar(dv);
            else if (prefix == "doc:move-to-")
                dv.Doc.MoveToCursor(0, move, dv);
            else if (prefix == "doc:select-to-")
                dv.Doc.SelectToCursor(0, move, dv);
            else
                dv.Doc.DeleteToCursor(0, move, dv);
            return true;
        }

        switch (name)
        {
            // core
            case "core:find-file":
                FindFile();
                return true;
            case "core:open-file":
                OpenFile();
                return true;
            case "core:new-doc":
                EditorCore.OpenDocView(EditorCore.OpenDoc(null));
                return true;

            // command
            case "command:submit":
                if (!command)
                    return false;
                EditorCore.Command.Submit();
                return true;
            case "command:complete":
                if (!command)
                    return false;
                EditorCore.Command.Complete();
                return true;
            case "command:escape":
                if (!command)
                    return false;
                EditorCore.Command.Exit(false, false);
                return true;
            case "command:select-previous":
                if (!command)
                    return false;
                EditorCore.Command.MoveSuggestionIdx(1);
                return true;
            case "command:select-next":
                if (!command)
                    return false;
                EditorCore.Command.MoveSuggestionIdx(-1);
                return true;

            // dialog
            case "dialog:previous-entry":
                if (!nag)
                    return false;
                EditorCore.Nag.PreviousEntry();
                return true;
            case "dialog:next-entry":
                if (!nag)
                    return false;
                EditorCore.Nag.NextEntry();
                return true;
            case "dialog:select-yes":
                if (!nag)
                    return false;
                EditorCore.Nag.SelectYes();
                return true;
            case "dialog:select-no":
                if (!nag)
                    return false;
                EditorCore.Nag.SelectNo();
                return true;
            case "dialog:select":
                if (!nag)
                    return false;
                EditorCore.Nag.Select();
                return true;

            // find-replace
            case "find-replace:find":
                if (window == null)
                    return false;
                StartFind(window);
                return true;
            case "find-replace:replace":
                if (window == null)
                    return false;
                StartReplace(window);
                return true;
            case "find-replace:repeat-find":
            case "find-replace:previous-find":
            {
                var target = command && EditorCore.ViewOf(lastFindView) != null ? EditorCore.ViewOf(lastFindView) : dv;
                if (target == null)
                    return false;
                RepeatFind(target, name == "find-replace:previous-find");
                return true;
            }
            case "find-replace:toggle-sensitivity":
                if (!command)
                    return false;
                caseSensitive = !caseSensitive;
                if (lastSel.Count == 4 && hasLastFind)
                    UpdatePreview(lastText);
                return true;
            case "find-replace:select-next":
            case "find-replace:select-previous":
            case "find-replace:select-add-next":
            case "find-replace:select-add-all":
            {
                var target = FindTarget();
                if (target == null || !HasUniqueSelection(target.Doc))
                    return false;
                if (name == "find-replace:select-next")
                    SelectNext(target.Doc, false);
                else if (name == "find-replace:select-previous")
                    SelectNext(target.Doc, true);
                else
                    SelectAddNext(target, name == "find-replace:select-add-all");
                return true;
            }
        }

        // doc の command は DocView (CommandView の入力欄も) で効く
        if (!name.StartsWith("doc:") || dv == null)
            return false;
        var doc = dv.Doc;
        switch (name)
        {
            case "doc:select-none":
            {
                var idx = doc.HasSelectionIdx(doc.LastSelection) ? doc.LastSelection : 1;
                doc.GetSelectionIdx(idx, false, out var l1, out var c1, out _, out _, out _);
                doc.SetSelection(l1, c1);
                return true;
            }
            case "doc:cut":
                CutOrCopy(doc, true);
                return true;
            case "doc:copy":
                CutOrCopy(doc, false);
                return true;
            case "doc:paste":
                Paste(doc);
                return true;
            case "doc:undo":
                doc.Undo();
                return true;
            case "doc:redo":
                doc.Redo();
                return true;
            case "doc:newline":
                Newline(doc);
                return true;
            case "doc:newline-below":
                for (var idx = doc.SelectionCount(); idx >= 1; idx--)
                {
                    doc.GetSelectionIdx(idx, false, out var line, out _, out _, out _, out _);
                    var indent = Str.Slice(doc.Lines[line - 1], 0, Str.IndentLength(doc.Lines[line - 1]));
                    doc.Insert(line, Str.Huge, "\n" + indent);
                    doc.SetCursor(idx, line + 1, Str.Huge);
                }
                return true;
            case "doc:newline-above":
                for (var idx = doc.SelectionCount(); idx >= 1; idx--)
                {
                    doc.GetSelectionIdx(idx, false, out var line, out _, out _, out _, out _);
                    var indent = Str.Slice(doc.Lines[line - 1], 0, Str.IndentLength(doc.Lines[line - 1]));
                    doc.Insert(line, 1, indent + "\n");
                    doc.SetCursor(idx, line, Str.Huge);
                }
                return true;
            case "doc:delete":
                for (var idx = doc.SelectionCount(); idx >= 1; idx--)
                {
                    if (!doc.HasSelectionIdx(idx))
                        continue;
                    doc.GetSelectionIdx(idx, true, out var l1, out var c1, out var l2, out var c2, out _);
                    if (l1 == l2 && c1 == c2 && Str.BlankFrom(doc.Lines[l1 - 1], c1 - 1))
                        doc.Remove(l1, c1, l1, Str.Huge);
                    doc.DeleteToCursor(idx, Move.NextChar, dv);
                }
                return true;
            case "doc:backspace":
                for (var idx = doc.SelectionCount(); idx >= 1; idx--)
                {
                    if (!doc.HasSelectionIdx(idx))
                        continue;
                    doc.GetSelectionIdx(idx, true, out var l1, out var c1, out var l2, out var c2, out _);
                    if (l1 == l2 && c1 == c2)
                    {
                        var text = doc.GetText(l1, 1, l1, c1);
                        if (Str.Len(text) >= Doc.IndentSize && text.Replace(" ", "") == "")
                        {
                            doc.DeleteToCursorLineCol(idx, 0, -Doc.IndentSize);
                            continue;
                        }
                    }
                    doc.DeleteToCursor(idx, Move.PreviousChar, dv);
                }
                return true;
            case "doc:select-all":
                doc.SetSelection(1, 1, Str.Huge, Str.Huge);
                dv.ForgetSelection();
                return true;
            case "doc:select-lines":
                for (var idx = 1; idx <= doc.SelectionCount(); idx++)
                {
                    doc.GetSelectionIdx(idx, true, out var l1, out _, out var l2, out _, out _);
                    AppendLineIfLastLine(doc, l2);
                    doc.SetSelections(idx, l1, 1, l2 + 1, 1);
                }
                return true;
            case "doc:select-word":
                for (var idx = 1; idx <= doc.SelectionCount(); idx++)
                {
                    doc.GetSelectionIdx(idx, true, out var l1, out var c1, out _, out _, out _);
                    doc.Translate(Move.StartOfWord, dv, l1, c1, out var sl, out var sc);
                    doc.Translate(Move.EndOfWord, dv, sl, sc, out var el, out var ec);
                    doc.SetSelections(idx, el, ec, sl, sc);
                }
                return true;
            case "doc:join-lines":
                JoinLines(doc);
                return true;
            case "doc:indent":
            case "doc:unindent":
                for (var idx = 1; idx <= doc.SelectionCount(); idx++)
                {
                    MultilineSelection(doc, idx, out var l1, out var c1, out var l2, out var c2);
                    doc.IndentText(name == "doc:unindent", l1, c1, l2, c2, out var r1, out var rc1, out var r2, out var rc2);
                    doc.SetSelections(idx, r1, rc1, r2, rc2);
                }
                return true;
            case "doc:duplicate-lines":
                for (var idx = 1; idx <= doc.SelectionCount(); idx++)
                {
                    MultilineSelection(doc, idx, out var l1, out var c1, out var l2, out var c2);
                    AppendLineIfLastLine(doc, l2);
                    var text = doc.GetText(l1, 1, l2 + 1, 1);
                    doc.Insert(l2 + 1, 1, text);
                    var n = l2 - l1 + 1;
                    doc.SetSelections(idx, l1 + n, c1, l2 + n, c2);
                }
                return true;
            case "doc:delete-lines":
                for (var idx = 1; idx <= doc.SelectionCount(); idx++)
                {
                    MultilineSelection(doc, idx, out var l1, out var c1, out var l2, out _);
                    AppendLineIfLastLine(doc, l2);
                    doc.Remove(l1, 1, l2 + 1, 1);
                    doc.SetCursor(idx, l1, c1);
                }
                return true;
            case "doc:move-lines-up":
                for (var idx = 1; idx <= doc.SelectionCount(); idx++)
                {
                    MultilineSelection(doc, idx, out var l1, out var c1, out var l2, out var c2);
                    AppendLineIfLastLine(doc, l2);
                    if (l1 > 1)
                    {
                        var text = doc.Lines[l1 - 2] + "\n";
                        doc.Insert(l2 + 1, 1, text);
                        doc.Remove(l1 - 1, 1, l1, 1);
                        doc.SetSelections(idx, l1 - 1, c1, l2 - 1, c2);
                    }
                }
                return true;
            case "doc:move-lines-down":
                for (var idx = 1; idx <= doc.SelectionCount(); idx++)
                {
                    MultilineSelection(doc, idx, out var l1, out var c1, out var l2, out var c2);
                    AppendLineIfLastLine(doc, l2 + 1);
                    if (l2 < doc.Lines.Count)
                    {
                        var text = doc.Lines[l2] + "\n";
                        doc.Remove(l2 + 1, 1, l2 + 2, 1);
                        doc.Insert(l1, 1, text);
                        doc.SetSelections(idx, l1 + 1, c1, l2 + 1, c2);
                    }
                }
                return true;
            case "doc:toggle-line-comments":
                if (!doc.Highlighter.CSharp)
                    return true;
                for (var idx = 1; idx <= doc.SelectionCount(); idx++)
                {
                    MultilineSelection(doc, idx, out var l1, out var c1, out var l2, out var c2);
                    LineComment(doc, l1, c1, l2, c2, out var r1, out var rc1, out var r2, out var rc2);
                    doc.SetSelections(idx, r1, rc1, r2, rc2);
                }
                return true;
            case "doc:toggle-block-comments":
                if (!doc.Highlighter.CSharp)
                    return true;
                for (var idx = 1; idx <= doc.SelectionCount(); idx++)
                {
                    MultilineSelection(doc, idx, out var l1, out var c1, out var l2, out var c2);
                    if (l1 == l2 && c1 == c2)
                    {
                        c1 = 1;
                        c2 = doc.LineSize(l2);
                    }
                    BlockComment(doc, l1, c1, l2, c2, out var r1, out var rc1, out var r2, out var rc2);
                    doc.SetSelections(idx, r1, rc1, r2, rc2);
                }
                return true;
            case "doc:go-to-line":
                if (window == null || command)
                    return false;
                EditorCore.Command.GoToView = window.Id;
                EditorCore.Command.Enter("Go To Line", CommandView.GoToLine, null, false, true);
                return true;
            case "doc:save":
                if (window == null)
                    return false;
                if (doc.Filename != null)
                    SaveDoc(doc, null);
                else
                    SaveAs(window);
                return true;
            case "doc:save-as":
                if (window == null)
                    return false;
                SaveAs(window);
                return true;
            case "doc:set-cursor":
            case "doc:set-cursor-word":
            case "doc:set-cursor-line":
            case "doc:split-cursor":
            {
                if (!InView(dv, x, y))
                    return false;
                dv.ResolveScreenPosition(x, y, out var line, out var col);
                if (name == "doc:split-cursor")
                {
                    var removal = 0;
                    for (var idx = 1; idx <= doc.SelectionCount(); idx++)
                    {
                        doc.GetSelectionIdx(idx, true, out var l1, out var c1, out _, out _, out _);
                        if (l1 == line && c1 == col && doc.SelectionCount() > 1)
                            removal = idx;
                    }
                    if (removal != 0)
                        doc.RemoveSelection(removal);
                    else
                        doc.AddSelection(line, col, line, col);
                    dv.StartMouseSelection(line, col, 1);
                    return true;
                }
                doc.SetSelection(line, col, line, col);
                var snap = name == "doc:set-cursor-word" ? 2 : name == "doc:set-cursor-line" ? 3 : 1;
                if (snap == 2)
                    Perform("doc:select-word");
                else if (snap == 3)
                    Perform("doc:select-lines");
                dv.StartMouseSelection(line, col, snap);
                return true;
            }
            case "doc:create-cursor-previous-line":
            case "doc:create-cursor-next-line":
                SplitCursor(dv, name == "doc:create-cursor-previous-line" ? -1 : 1);
                doc.MergeCursors();
                return true;
        }
        return false;
    }

    static bool InView(DocView dv, float x, float y)
    {
        return x >= dv.PosX + dv.GutterWidth() && x < dv.PosX + dv.SizeX && y >= dv.PosY && y < dv.PosY + dv.SizeY;
    }

    static bool SelectToCursor(DocView dv, float x, float y)
    {
        var doc = dv.Doc;
        doc.GetSelection(false, out _, out _, out var line1, out var col1);
        dv.ResolveScreenPosition(x, y, out var line2, out var col2);
        dv.StartMouseSelection(line1, col1, 0);
        doc.SetSelection(line2, col2, line1, col1);
        return true;
    }

    static void MoveToPreviousChar(DocView dv)
    {
        var doc = dv.Doc;
        for (var idx = 1; idx <= doc.SelectionCount(); idx++)
        {
            doc.GetSelectionIdx(idx, true, out var l1, out var c1, out var l2, out var c2, out _);
            if (l1 != l2 || c1 != c2)
                doc.SetCursor(idx, l1, c1);
            else
                doc.MoveToCursor(idx, Move.PreviousChar, dv);
        }
        doc.MergeCursors();
    }

    static void MoveToNextChar(DocView dv)
    {
        var doc = dv.Doc;
        for (var idx = 1; idx <= doc.SelectionCount(); idx++)
        {
            doc.GetSelectionIdx(idx, true, out var l1, out var c1, out var l2, out var c2, out _);
            if (l1 != l2 || c1 != c2)
                doc.SetCursor(idx, l2, c2);
            else
                doc.MoveToCursor(idx, Move.NextChar, dv);
        }
        doc.MergeCursors();
    }

    // 選択の最後が次の行の先頭なら、前の行の末尾までにする
    static void MultilineSelection(Doc doc, int idx, out int l1, out int c1, out int l2, out int c2)
    {
        doc.GetSelectionIdx(idx, true, out var a, out var b, out var c, out var d, out _);
        if (c > a && d == 1)
        {
            c--;
            d = doc.LineSize(c);
        }
        l1 = a;
        c1 = b;
        l2 = c;
        c2 = d;
    }

    static void AppendLineIfLastLine(Doc doc, int line)
    {
        if (line >= doc.Lines.Count)
            doc.Insert(line, Str.Huge, "\n");
    }

    static void Newline(Doc doc)
    {
        for (var idx = doc.SelectionCount(); idx >= 1; idx--)
        {
            if (!doc.HasSelectionIdx(idx))
                continue;
            doc.GetSelectionIdx(idx, false, out var line, out var col, out _, out _, out _);
            var text = doc.Lines[line - 1];
            var indentLen = Str.IndentLength(text);
            var indent = Str.Slice(text, 0, indentLen);
            if (col <= indentLen)
                indent = Str.Sub(indent, indentLen + 2 - col, -1);
            // 空白だけの行は空にする
            if (text != "" && Str.FirstInk(text) < 0)
                doc.Remove(line, 1, line, Str.Huge);
            doc.TextInput("\n" + indent, idx);
        }
    }

    static void JoinLines(Doc doc)
    {
        for (var idx = 1; idx <= doc.SelectionCount(); idx++)
        {
            doc.GetSelectionIdx(idx, true, out var l1, out var c1, out var l2, out var c2, out _);
            if (l1 == l2)
                l2++;
            var text = doc.GetText(l1, 1, l2, Str.Huge);
            // 改行と次の行の字下げを、空白 1 つにする (空白だけの行の後は空白を足さない)
            var parts = text.Split('\n');
            var joined = parts[0];
            var prevBlank = Str.FirstInk(parts[0]) < 0;
            for (var i = 1; i < parts.Length; i++)
            {
                var part = Str.From(parts[i], Str.IndentLength(parts[i]));
                joined += (prevBlank ? "" : " ") + part;
                prevBlank = Str.FirstInk(part) < 0;
            }
            doc.Insert(l1, 1, joined);
            doc.Remove(l1, Str.Len(joined) + 1, l2, Str.Huge);
            if (l1 != l2 || c1 != c2)
                doc.SetCursor(idx, l1, Str.Huge);
        }
    }

    static void LineComment(Doc doc, int line1, int col1, int line2, int col2, out int r1, out int rc1, out int r2, out int rc2)
    {
        var start = "// ";
        var startLen = Str.Len(start);
        var uncomment = true;
        var startOffset = Str.Huge;
        for (var line = line1; line <= line2; line++)
        {
            var text = doc.Lines[line - 1];
            var s = Str.FirstInk(text);
            if (s >= 0)
            {
                if (Str.Find(text, start, s) != s)
                    uncomment = false;
                startOffset = Math.Min(startOffset, s + 1);
            }
        }
        for (var line = line1; line <= line2; line++)
        {
            var text = doc.Lines[line - 1];
            var s = Str.FirstInk(text);
            if (s >= 0 && uncomment)
            {
                var cs = Str.Find(text, start, s);
                if (cs >= 0)
                    doc.Remove(line, cs + 1, line, cs + startLen + 1);
            }
            else if (s >= 0)
                doc.Insert(line, startOffset, start);
        }
        var sign = uncomment ? -1 : 1;
        col1 += (col1 > startOffset ? startLen : 0) * sign;
        col2 += (col2 > startOffset ? startLen : 0) * sign;
        r1 = line1;
        rc1 = col1;
        r2 = line2;
        rc2 = col2;
    }

    static void BlockComment(Doc doc, int line1, int col1, int line2, int col2, out int r1, out int rc1, out int r2, out int rc2)
    {
        var open = "/*";
        var close = "*/";
        // 前後の空白を飛ばす
        var head = doc.GetText(line1, col1, line1, Str.Huge);
        var ink = Str.FirstInk(head);
        col1 += ink >= 0 ? ink : 0;
        var tail = doc.GetText(line2, 1, line2, col2);
        var tl = Str.Len(tail);
        while (tl > 0 && Str.IsSpace(Str.Byte(tail, tl - 1)))
            tl--;
        col2 = tl + 1;
        var blockStart = doc.GetText(line1, col1, line1, col1 + 2);
        var blockEnd = doc.GetText(line2, col2 - 2, line2, col2);
        if (blockStart == open && blockEnd == close)
        {
            var startLen = 2;
            var stopLen = 2;
            var after = doc.GetText(line1, col1 + 2, line1, col1 + 3);
            if (after != "" && Str.IsSpace(Str.Byte(after, 0)))
                startLen++;
            var before = doc.GetText(line2, col2 - 3, line2, col2);
            if (before != "" && Str.IsSpace(Str.Byte(before, 0)))
                stopLen++;
            doc.Remove(line1, col1, line1, col1 + startLen);
            col2 -= line1 == line2 ? startLen : 0;
            doc.Remove(line2, col2 - stopLen, line2, col2);
            r1 = line1;
            rc1 = col1;
            r2 = line2;
            rc2 = col2 - stopLen;
            return;
        }
        doc.Insert(line1, col1, open + " ");
        col2 += line1 == line2 ? 3 : 0;
        doc.Insert(line2, col2, " " + close);
        r1 = line1;
        rc1 = col1;
        r2 = line2;
        rc2 = col2 + 3;
    }

    static void CutOrCopy(Doc doc, bool delete)
    {
        var full = "";
        var n = doc.SelectionCount();
        var clips = new List<string>();
        var whole = new List<bool>();
        for (var i = 0; i < n; i++)
        {
            clips.Add("");
            whole.Add(false);
        }
        for (var idx = n; idx >= 1; idx--)
        {
            if (!doc.HasSelectionIdx(idx))
                continue;
            doc.GetSelectionIdx(idx, true, out var l1, out var c1, out var l2, out var c2, out _);
            string text;
            if (l1 != l2 || c1 != c2)
            {
                text = doc.GetText(l1, c1, l2, c2);
                full = full == "" ? text : text + " " + full;
                whole[idx - 1] = false;
                if (delete)
                    doc.DeleteToCursorLineCol(idx, 0, 0);
            }
            else
            {
                // 選んでいなければ行を丸ごと
                text = doc.Lines[l1 - 1];
                full = full == "" ? text + "\n" : text + "\n" + full;
                whole[idx - 1] = true;
                if (delete)
                {
                    if (l1 < doc.Lines.Count)
                        doc.Remove(l1, 1, l1 + 1, 1);
                    else if (doc.Lines.Count == 1)
                        doc.Remove(l1, 1, l1, Str.Huge);
                    else
                        doc.Remove(l1 - 1, Str.Huge, l1, Str.Huge);
                    doc.SetSelections(idx, l1, c1, l2, c2);
                }
            }
            clips[idx - 1] = text;
        }
        if (delete)
            doc.MergeCursors();
        EditorCore.CursorClipboard = clips;
        EditorCore.CursorClipboardWholeLine = whole;
        EditorCore.CursorClipboardFull = full;
        EditorCore.Clipboard = full;
    }

    static void InsertPaste(Doc doc, string value, bool wholeLine, int idx)
    {
        value = value.Replace("\r", "");
        if (wholeLine)
        {
            doc.GetSelectionIdx(idx, false, out var l1, out var c1, out _, out _, out _);
            doc.Insert(l1, 1, value + "\n");
            if (c1 == 1)
                doc.MoveToCursorBytes(idx, Str.Len(value) + 1);
        }
        else
            doc.TextInput(value, idx);
    }

    static void Paste(Doc doc)
    {
        var clipboard = EditorCore.Clipboard;
        if (EditorCore.CursorClipboardFull != clipboard || EditorCore.CursorClipboard.Count == 0)
        {
            EditorCore.CursorClipboard = new List<string>();
            EditorCore.CursorClipboardWholeLine = new List<bool>();
            for (var idx = 1; idx <= doc.SelectionCount(); idx++)
                InsertPaste(doc, clipboard, false, idx);
            return;
        }
        var onlyWhole = true;
        foreach (var w in EditorCore.CursorClipboardWholeLine)
            if (!w)
                onlyWhole = false;
        if (EditorCore.CursorClipboardWholeLine.Count == doc.SelectionCount())
        {
            for (var idx = 1; idx <= doc.SelectionCount(); idx++)
                InsertPaste(doc, EditorCore.CursorClipboard[idx - 1], onlyWhole, idx);
            return;
        }
        var selections = new List<int>();
        for (var idx = 1; idx <= doc.SelectionCount(); idx++)
        {
            for (var cb = 0; cb < EditorCore.CursorClipboard.Count; cb++)
            {
                InsertPaste(doc, EditorCore.CursorClipboard[cb], onlyWhole, idx);
                if (!onlyWhole)
                    AddSel(doc, idx, selections);
            }
            if (onlyWhole)
                AddSel(doc, idx, selections);
        }
        for (var i = 0; i + 3 < selections.Count; i += 4)
        {
            if (i == 0)
                doc.SetSelection(selections[0], selections[1], selections[2], selections[3]);
            else
                doc.AddSelection(selections[i], selections[i + 1], selections[i + 2], selections[i + 3]);
        }
    }

    static void AddSel(Doc doc, int idx, List<int> list)
    {
        doc.GetSelectionIdx(idx, false, out var a, out var b, out var c, out var d, out _);
        list.Add(a);
        list.Add(b);
        list.Add(c);
        list.Add(d);
    }

    static void SplitCursor(DocView dv, int direction)
    {
        var doc = dv.Doc;
        var lines = new List<int>();
        var cols = new List<int>();
        for (var idx = 1; idx <= doc.SelectionCount(); idx++)
        {
            doc.GetSelectionIdx(idx, false, out var l1, out var c1, out _, out _, out _);
            if (l1 + direction >= 1 && l1 + direction <= doc.Lines.Count)
            {
                doc.Translate(direction < 0 ? Move.PreviousLine : Move.NextLine, dv, l1, c1, out var l, out var c);
                lines.Add(l);
                cols.Add(c);
            }
        }
        if (direction < 0)
            for (var i = lines.Count - 1; i >= 0; i--)
                doc.AddSelection(lines[i], cols[i], lines[i], cols[i]);
        else
            for (var i = 0; i < lines.Count; i++)
                doc.AddSelection(lines[i], cols[i], lines[i], cols[i]);
    }

    // 保存 ------------------------------------------------------------------

    static void SaveDoc(Doc doc, string? filename)
    {
        string? abs = null;
        if (filename != null)
        {
            filename = Paths.ToProject(filename);
            abs = Paths.Absolute(filename);
        }
        if (!doc.Save(filename, abs))
            EditorCore.Error("no filename set to default to");
    }

    static void SaveAs(DocView dv)
    {
        string? text = null;
        var last = EditorCore.ViewOf(EditorCore.LastActive);
        if (dv.Doc.Filename != null)
            text = dv.Doc.Filename;
        else if (last != null && last.Doc.AbsFilename != null)
        {
            var dir = Paths.Dirname(last.Doc.AbsFilename);
            if (dir != null)
                text = Paths.ToProject(dir) + "/";
        }
        EditorCore.Command.Enter("Save As", CommandView.SaveAs, text, false, true);
    }

    static void OpenFile()
    {
        string? text = null;
        var dv = EditorCore.ActiveWindowView();
        if (dv != null && dv.Doc.AbsFilename != null)
        {
            var dir = Paths.Dirname(dv.Doc.AbsFilename);
            if (dir != null)
            {
                dir = Paths.ToProject(dir);
                text = dir == Paths.ProjectDir ? "" : dir + "/";
            }
        }
        EditorCore.Command.Enter("Open File", CommandView.OpenFile, text, false, true);
    }

    static void FindFile()
    {
        if (Paths.ProjectFiles() == null)
        {
            OpenFile();
            return;
        }
        EditorCore.Command.Enter("Open File From Project", CommandView.FindFile, null, false, true);
    }

    // CommandView で Return を押した
    public static void SubmitCommand(int kind, string text, Suggestion? item, string replaceOld, int goToView)
    {
        if (kind == CommandView.Find)
        {
            EditorCore.AddPreviousFind(EditorCore.PreviousFind, text);
            var view = EditorCore.ViewOf(lastFindView);
            if (foundExpression)
            {
                hasLastFind = true;
                lastText = text;
            }
            else
            {
                EditorCore.Error("Couldn't find \"" + text + "\"");
                if (view != null && lastSel.Count == 4)
                {
                    view.Doc.SetSelection(lastSel[0], lastSel[1], lastSel[2], lastSel[3]);
                    view.ScrollToMakeVisible(lastSel[0], lastSel[1]);
                }
            }
        }
        else if (kind == CommandView.ReplaceFind)
        {
            EditorCore.AddPreviousFind(EditorCore.PreviousFind, text);
            EditorCore.Command.ReplaceOld = text;
            EditorCore.Command.Enter("Replace Text \"" + text + "\" With", CommandView.ReplaceWith, text, true, false);
        }
        else if (kind == CommandView.ReplaceWith)
        {
            EditorCore.AddPreviousFind(EditorCore.PreviousReplace, text);
            var dv = FindTarget();
            if (dv != null && replaceOld != "")
                Replace(dv.Doc, replaceOld, text);
        }
        else if (kind == CommandView.GoToLine)
        {
            var view = EditorCore.ViewOf(goToView);
            var line = item != null && item.Line > 0 ? item.Line : 0;
            if (line == 0 && !int.TryParse(text, out line))
                line = 0;
            if (line <= 0 && item == null)
            {
                EditorCore.Error("Invalid line number or unmatched string");
                return;
            }
            if (view != null)
            {
                view.Doc.SetSelection(line, 1);
                view.ScrollToLine(line, true, false);
            }
        }
        else if (kind == CommandView.OpenFile)
        {
            string? filename = null;
            if (Paths.Exists(text))
            {
                Disk.AbsolutePath(text, out var abs);
                filename = abs;
            }
            EditorCore.OpenDocView(EditorCore.OpenDoc(filename));
        }
        else if (kind == CommandView.FindFile)
        {
            var name = item != null ? item.Text : text;
            EditorCore.OpenDocView(EditorCore.OpenDoc(name));
        }
        else if (kind == CommandView.SaveAs)
        {
            var dv = EditorCore.ActiveWindowView();
            if (dv != null)
                SaveDoc(dv.Doc, text);
        }
    }

    // CommandView を閉じた (explicit は Escape で閉じたか)
    public static void CancelCommand(int kind, bool explicitly)
    {
        if (kind == CommandView.Find && explicitly)
        {
            var view = EditorCore.ViewOf(lastFindView);
            if (view != null && lastSel.Count == 4)
            {
                view.Doc.SetSelection(lastSel[0], lastSel[1], lastSel[2], lastSel[3]);
                view.ScrollToMakeVisible(lastSel[0], lastSel[1]);
            }
        }
    }

    // 検索 ------------------------------------------------------------------

    // 検索の対象 (CommandView から呼んだなら、検索を始めた view)
    static DocView? FindTarget()
    {
        var w = EditorCore.ActiveWindowView();
        if (w != null)
            return w;
        return EditorCore.ViewOf(lastFindView);
    }

    static void StartFind(DocView dv)
    {
        lastFindView = dv.Id;
        dv.Doc.GetSelection(false, out var l1, out var c1, out var l2, out var c2);
        lastSel = new List<int> { l1, c1, l2, c2 };
        var text = dv.Doc.GetText(l1, c1, l2, c2);
        foundExpression = false;
        EditorCore.Command.Enter("Find Text", CommandView.Find, text, true, false);
    }

    static void StartReplace(DocView dv)
    {
        lastFindView = dv.Id;
        dv.Doc.GetSelection(false, out var l1, out var c1, out var l2, out var c2);
        var selected = dv.Doc.GetText(l1, c1, l2, c2);
        EditorCore.Command.Enter("Find To Replace Text", CommandView.ReplaceFind, l1 == l2 ? selected : "", true, false);
    }

    // 打った文字を、検索を始めた所から探して選ぶ
    public static void UpdatePreview(string text)
    {
        var view = EditorCore.ViewOf(lastFindView);
        if (view == null || lastSel.Count != 4)
            return;
        if (text != "" && view.Doc.Find(lastSel[0], lastSel[1], text, !caseSensitive, true, false, out var l1, out var c1, out var l2, out var c2))
        {
            view.Doc.SetSelection(l2, c2, l1, c1);
            view.ScrollToLine(l2, true, false);
            foundExpression = true;
        }
        else
        {
            view.Doc.SetSelection(lastSel[0], lastSel[1], lastSel[2], lastSel[3]);
            foundExpression = false;
        }
        hasLastFind = true;
        lastText = text;
    }

    static void RepeatFind(DocView dv, bool reverse)
    {
        if (!hasLastFind)
        {
            EditorCore.Error("No find to continue from");
            return;
        }
        dv.Doc.GetSelection(true, out var sl1, out var sc1, out var sl2, out var sc2);
        var line = reverse ? sl1 : sl2;
        var col = reverse ? sc1 : sc2;
        if (dv.Doc.Find(line, col, lastText, !caseSensitive, true, reverse, out var l1, out var c1, out var l2, out var c2))
        {
            dv.Doc.SetSelection(l2, c2, l1, c1);
            dv.ScrollToLine(l2, true, false);
        }
        else
            EditorCore.Error("Couldn't find \"" + lastText + "\"");
    }

    static bool HasUniqueSelection(Doc doc)
    {
        string? text = null;
        for (var idx = doc.SelectionCount(); idx >= 1; idx--)
        {
            doc.GetSelectionIdx(idx, true, out var l1, out var c1, out var l2, out var c2, out _);
            if (l1 == l2 && c1 == c2)
                return false;
            var s = doc.GetText(l1, c1, l2, c2);
            if (text != null && text != s)
                return false;
            text = s;
        }
        return text != null;
    }

    static void SelectNext(Doc doc, bool reverse)
    {
        doc.GetSelection(true, out var l1, out var c1, out var l2, out var c2);
        var text = doc.GetText(l1, c1, l2, c2);
        var line = reverse ? l1 : l2;
        var col = reverse ? c1 : c2;
        if (doc.Find(line, col, text, false, true, reverse, out var a, out var b, out var c, out var d))
            doc.SetSelection(c, d, a, b);
    }

    static bool InSelection(int line, int col, int l1, int c1, int l2, int c2)
    {
        if (line < l1 || line > l2)
            return false;
        if (line == l1 && col <= c1)
            return false;
        if (line == l2 && col > c2)
            return false;
        return true;
    }

    static bool InAnySelection(Doc doc, int line, int col)
    {
        for (var idx = 1; idx <= doc.SelectionCount(); idx++)
        {
            doc.GetSelectionIdx(idx, true, out var l1, out var c1, out var l2, out var c2, out _);
            if (InSelection(line, col, l1, c1, l2, c2))
                return true;
        }
        return false;
    }

    static void SelectAddNext(DocView dv, bool all)
    {
        var doc = dv.Doc;
        var il1 = -1;
        var ic1 = -1;
        for (var idx = doc.SelectionCount(); idx >= 1; idx--)
        {
            doc.GetSelectionIdx(idx, true, out var l1, out var c1, out var l2, out var c2, out _);
            if (il1 < 0)
            {
                il1 = l1;
                ic1 = c1;
            }
            var text = doc.GetText(l1, c1, l2, c2);
            while (true)
            {
                var found = doc.Find(l2, c2, text, false, true, false, out var f1, out var fc1, out var f2, out var fc2);
                if (!found)
                    break;
                l1 = f1;
                c1 = fc1;
                l2 = f2;
                c2 = fc2;
                if (l1 == il1 && c1 == ic1)
                    break;
                if (!InAnySelection(doc, l2, c2))
                {
                    doc.AddSelection(l2, c2, l1, c1);
                    if (!all)
                    {
                        dv.ScrollToMakeVisible(l2, c2);
                        return;
                    }
                }
                if (!all)
                    break;
            }
            if (all)
                break;
        }
    }

    // 選んだ所 (無ければ全体) の old を new にする (大文字と小文字を区別する)
    static void Replace(Doc doc, string old, string with)
    {
        var any = false;
        for (var idx = 1; idx <= doc.SelectionCount(); idx++)
        {
            doc.GetSelectionIdx(idx, true, out var l1, out var c1, out var l2, out var c2, out _);
            if (l1 != l2 || c1 != c2)
            {
                ReplaceRange(doc, idx, l1, c1, l2, c2, old, with);
                any = true;
            }
        }
        if (!any)
        {
            doc.GetSelectionIdx(1, false, out var a, out var b, out var c, out var d, out _);
            doc.SetSelection(a, b, c, d);
            ReplaceRange(doc, 1, 1, 1, doc.Lines.Count, doc.LineSize(doc.Lines.Count), old, with);
        }
    }

    static void ReplaceRange(Doc doc, int idx, int line1, int col1, int line2, int col2, string old, string with)
    {
        var oldText = doc.GetText(line1, col1, line2, col2);
        var newText = oldText.Replace(old, with);
        if (oldText == newText)
            return;
        doc.Insert(line2, col2, newText);
        doc.Remove(line1, col1, line2, col2);
        if (line1 == line2 && col1 == col2)
        {
            doc.OffsetBytes(line1, col1, Str.Len(newText), out var e1, out var e2);
            doc.SetSelections(idx, line1, col1, e1, e2);
        }
    }
}
