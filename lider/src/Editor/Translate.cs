// cursor の動き (Lite XL の core/doc/translate.lua と、DocView.translate の行と頁の動き)。
using System;

namespace LiderEditor;

public enum Move
{
    None,
    PreviousChar,
    NextChar,
    PreviousWordStart,
    NextWordEnd,
    PreviousBlockStart,
    NextBlockEnd,
    StartOfDoc,
    EndOfDoc,
    StartOfLine,
    EndOfLine,
    StartOfWord,
    StartOfIndentation,
    EndOfWord,
    PreviousLine,
    NextLine,
    PreviousPage,
    NextPage,
}

public static class Translate
{
    // Run の結果。tcs VM は別の class の static method の out をまだ受けないので、結果は field で返す (Doc.Translate が out にする)
    public static int Line;
    public static int Col;
    // 中の関数の結果
    static int rl;
    static int rc;

    // 語の区切りの文字 (Lite XL の config.non_word_chars)
    const string NonWord = " \t\n/\\()\"':,.;<>~!@#$%^&*|+=[]{}`?-";

    static bool IsNonWord(int b)
    {
        if (b < 0 || b >= 0x80)
            return false;
        return NonWord.Contains(((char)b).ToString());
    }

    // command の名前の後ろ ("previous-char" など) から動き (無ければ None)
    public static Move Parse(string name)
    {
        switch (name)
        {
            case "previous-char": return Move.PreviousChar;
            case "next-char": return Move.NextChar;
            case "previous-word-start": return Move.PreviousWordStart;
            case "next-word-end": return Move.NextWordEnd;
            case "previous-block-start": return Move.PreviousBlockStart;
            case "next-block-end": return Move.NextBlockEnd;
            case "start-of-doc": return Move.StartOfDoc;
            case "end-of-doc": return Move.EndOfDoc;
            case "start-of-line": return Move.StartOfLine;
            case "end-of-line": return Move.EndOfLine;
            case "start-of-word": return Move.StartOfWord;
            case "start-of-indentation": return Move.StartOfIndentation;
            case "end-of-word": return Move.EndOfWord;
            case "previous-line": return Move.PreviousLine;
            case "next-line": return Move.NextLine;
            case "previous-page": return Move.PreviousPage;
            case "next-page": return Move.NextPage;
        }
        return Move.None;
    }

    // (line, col) を move で動かした所を Line と Col に置く (Doc.position_offset と同じく、先に範囲に収める)
    public static void Run(Doc doc, Move move, DocView? dv, int line, int col)
    {
        doc.SanitizePosition(line, col, out var sl, out var sc);
        line = sl;
        col = sc;
        switch (move)
        {
            case Move.PreviousChar:
                PreviousChar(doc, line, col);
                Line = rl;
                Col = rc;
                return;
            case Move.NextChar:
                NextChar(doc, line, col);
                Line = rl;
                Col = rc;
                return;
            case Move.PreviousWordStart:
                PreviousWordStart(doc, line, col);
                Line = rl;
                Col = rc;
                return;
            case Move.NextWordEnd:
                NextWordEnd(doc, line, col);
                Line = rl;
                Col = rc;
                return;
            case Move.PreviousBlockStart:
                PreviousBlockStart(doc, line);
                Line = rl;
                Col = rc;
                return;
            case Move.NextBlockEnd:
                NextBlockEnd(doc, line);
                Line = rl;
                Col = rc;
                return;
            case Move.StartOfDoc:
                Line = 1;
                Col = 1;
                return;
            case Move.EndOfDoc:
                Line = doc.Lines.Count;
                Col = doc.LineSize(Line);
                return;
            case Move.StartOfLine:
                Line = line;
                Col = 1;
                return;
            case Move.EndOfLine:
                Line = line;
                Col = Str.Huge;
                return;
            case Move.StartOfWord:
                StartOfWord(doc, line, col);
                Line = rl;
                Col = rc;
                return;
            case Move.EndOfWord:
                EndOfWord(doc, line, col);
                Line = rl;
                Col = rc;
                return;
            case Move.StartOfIndentation:
            {
                // Lite XL の ^%s* は改行も含むので、空白だけの行では長さ + 1 まで
                var s = doc.Lines[line - 1];
                var e = Str.FirstInk(s);
                if (e < 0)
                    e = Str.Len(s) + 1;
                Line = line;
                Col = col > e + 1 ? e + 1 : 1;
                return;
            }
            case Move.PreviousLine:
                if (line == 1 || dv == null)
                {
                    Line = 1;
                    Col = 1;
                    return;
                }
                dv.MoveToLineOffset(line, col, -1, out var ml, out var mc);
                Line = ml;
                Col = mc;
                return;
            case Move.NextLine:
                if (line == doc.Lines.Count || dv == null)
                {
                    Line = doc.Lines.Count;
                    Col = Str.Huge;
                    return;
                }
                dv.MoveToLineOffset(line, col, 1, out var ml, out var mc);
                Line = ml;
                Col = mc;
                return;
            case Move.PreviousPage:
            {
                var span = dv == null ? 0 : dv.LastVisibleLine() - dv.FirstVisibleLine();
                Line = line - span;
                Col = 1;
                return;
            }
            case Move.NextPage:
            {
                if (line == doc.Lines.Count)
                {
                    Line = line;
                    Col = doc.LineSize(line);
                    return;
                }
                var span = dv == null ? 0 : dv.LastVisibleLine() - dv.FirstVisibleLine();
                Line = line + span;
                Col = 1;
                return;
            }
        }
        Line = line;
        Col = col;
    }

    static void PreviousChar(Doc doc, int line, int col)
    {
        do
        {
            doc.OffsetBytes(line, col, -1, out var nl, out var nc);
            var moved = nl != line || nc != col;
            line = nl;
            col = nc;
            if (!moved)
                break;
        } while (Str.IsCont(doc.GetChar(line, col)));
        rl = line;
        rc = col;
    }

    static void NextChar(Doc doc, int line, int col)
    {
        do
        {
            doc.OffsetBytes(line, col, 1, out var nl, out var nc);
            var moved = nl != line || nc != col;
            line = nl;
            col = nc;
            if (!moved)
                break;
        } while (Str.IsCont(doc.GetChar(line, col)));
        rl = line;
        rc = col;
    }

    static void PreviousWordStart(Doc doc, int line, int col)
    {
        var prev = -1;
        while (line > 1 || col > 1)
        {
            doc.OffsetBytes(line, col, -1, out var pl, out var pc);
            var ch = doc.GetChar(pl, pc);
            if ((prev >= 0 && prev != ch) || !IsNonWord(ch))
                break;
            prev = ch;
            line = pl;
            col = pc;
        }
        StartOfWord(doc, line, col);
    }

    static void NextWordEnd(Doc doc, int line, int col)
    {
        var prev = -1;
        var endLine = doc.Lines.Count;
        var endCol = doc.LineSize(endLine);
        while (line < endLine || col < endCol)
        {
            var ch = doc.GetChar(line, col);
            if ((prev >= 0 && prev != ch) || !IsNonWord(ch))
                break;
            doc.OffsetBytes(line, col, 1, out var nl, out var nc);
            line = nl;
            col = nc;
            prev = ch;
        }
        EndOfWord(doc, line, col);
    }

    static void StartOfWord(Doc doc, int line, int col)
    {
        while (true)
        {
            doc.OffsetBytes(line, col, -1, out var l2, out var c2);
            var ch = doc.GetChar(l2, c2);
            if (IsNonWord(ch) || (line == l2 && col == c2))
                break;
            line = l2;
            col = c2;
        }
        rl = line;
        rc = col;
    }

    static void EndOfWord(Doc doc, int line, int col)
    {
        while (true)
        {
            doc.OffsetBytes(line, col, 1, out var l2, out var c2);
            var ch = doc.GetChar(line, col);
            if (IsNonWord(ch) || (line == l2 && col == c2))
                break;
            line = l2;
            col = c2;
        }
        rl = line;
        rc = col;
    }

    static bool Blank(Doc doc, int line)
    {
        return Str.FirstInk(doc.Lines[line - 1]) < 0;
    }

    static void PreviousBlockStart(Doc doc, int line)
    {
        while (true)
        {
            line--;
            if (line <= 1)
            {
                rl = 1;
                rc = 1;
                return;
            }
            if (Blank(doc, line - 1) && !Blank(doc, line))
            {
                rl = line;
                rc = Str.FirstInk(doc.Lines[line - 1]) + 1;
                return;
            }
        }
    }

    static void NextBlockEnd(Doc doc, int line)
    {
        while (true)
        {
            if (line >= doc.Lines.Count)
            {
                rl = doc.Lines.Count;
                rc = 1;
                return;
            }
            if (Blank(doc, line + 1) && !Blank(doc, line))
            {
                rl = line + 1;
                rc = doc.LineSize(line + 1);
                return;
            }
            line++;
        }
    }
}
