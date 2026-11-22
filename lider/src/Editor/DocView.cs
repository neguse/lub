// View that draws a doc (a port of Lite XL's core/docview.lua and core/view.lua).
// One per window, and one for the CommandView input field (Bar).
// Positions are px within the view. Scrolling is not animated and moves to the destination at once (lider redraws only when something changes).
using System;
using System.Collections.Generic;

namespace LiderEditor;

public class DocView
{
    public int Id;
    public Doc Doc;
    // Whether this is the CommandView input field (1 line, label in the gutter, no scrolling)
    public readonly bool Bar;
    public string Label = "";
    public float BarGutter;

    public float PosX;
    public float PosY;
    public float SizeX = 800;
    public float SizeY = 600;
    public float ScrollX;
    public float ScrollY;
    public float ToX;
    public float ToY;
    // Fractional part of Host.Scroll (px)
    public float ScrollRest;

    // Selection at the previous update (if it changes, scrolls so the cursor is visible)
    int lastLine1 = -1;
    int lastCol1;
    int lastLine2;
    int lastCol2;
    // x before moving up or down (returns to the earlier column even after passing short lines)
    int xoLine = -1;
    int xoCol;
    float xoOffset;

    public bool HoveringGutter;
    // While selecting with the button held: the pressed position and the selection mode (0 = none, 1 = character, 2 = word, 3 = line)
    public bool MouseSelecting;
    int msLine;
    int msCol;
    int msSnap;

    // Selected part within the composing text (byte)
    public bool ImeStatus;
    public int ImeFrom;
    public int ImeSize;

    // Number of lines left empty above a line (where lider inserts lines that are not text). gaps[line] is above line line (from 1), gaps[line count + 1] is below the last line (0 where there is none).
    // gapsThrough[line] is the sum of gaps from line 1 to line line
    int[] gaps = new int[0];
    int[] gapsThrough = new int[0];
    // Goes up every time gaps changes (line heights change, so everything drawn is redrawn)
    public int GapsVersion;

    // Top-left of the cursors drawn by the last Draw (a sequence of x, y)
    public readonly List<float> Carets = new List<float>();
    // A number that goes up every time the appearance changes, and the string that determines it
    public int Version;
    public string Signature = "";

    public DocView(Doc doc, bool bar)
    {
        Doc = doc;
        Bar = bar;
    }

    public int Font()
    {
        return Bar ? Style.Font : Style.CodeFont;
    }

    public float LineHeight()
    {
        return (float)Math.Floor(DrawList.FontHeight() * (Bar ? 1.2f : 1.0f));
    }

    // Whether to draw line numbers (turned off while lider shows load, since it draws the load numbers where the line numbers are), and the minimum width of the line number column
    public static bool LineNumbers = true;
    public static float MinNumberWidth;

    public float GutterWidth()
    {
        if (Bar)
            return BarGutter;
        return Math.Max(DrawList.Width(Doc.Lines.Count.ToString()), MinNumberWidth) + Style.PaddingX * 2;
    }

    public float GutterPadding()
    {
        return Bar ? 0 : Style.PaddingX * 2;
    }

    public float ContentOffsetX()
    {
        return DrawList.Round(PosX - ScrollX);
    }

    public float ContentOffsetY()
    {
        return DrawList.Round(PosY - ScrollY);
    }

    // Top-left of line line (the left edge of the text)
    public float LineX()
    {
        return ContentOffsetX() + GutterWidth();
    }

    public float LineY(int line)
    {
        if (Bar)
            return ContentOffsetY() + (SizeY - LineHeight()) / 2;
        return ContentOffsetY() + Row(line) * LineHeight() + Style.PaddingY;
    }

    public void SetGaps(int[] g)
    {
        var same = g.Length == gaps.Length;
        for (var i = 0; same && i < g.Length; i++)
            same = g[i] == gaps[i];
        if (same)
            return;
        gaps = new int[g.Length];
        gapsThrough = new int[g.Length];
        var sum = 0;
        for (var i = 0; i < g.Length; i++)
        {
            gaps[i] = g[i];
            if (i > 0)
                sum += g[i];
            gapsThrough[i] = sum;
        }
        GapsVersion++;
    }

    // Number of lines left empty above line line
    public int Gap(int line)
    {
        return line >= 1 && line < gaps.Length ? gaps[line] : 0;
    }

    // Which line from the top of the content the top edge of line line is at (including empty lines, from 0)
    public int Row(int line)
    {
        if (gapsThrough.Length == 0 || line < 1)
            return line - 1;
        return line - 1 + gapsThrough[Math.Min(line, gapsThrough.Length - 1)];
    }

    public float ColX(int line, int col)
    {
        return LineX() + ColXOffset(line, col);
    }

    public float ScrollableSize()
    {
        if (Bar)
            return 0;
        return LineHeight() * Row(Doc.Lines.Count + 1) - LineHeight() + SizeY;
    }

    // The first line whose bottom edge is below the scroll position
    public int FirstVisibleLine()
    {
        var lh = LineHeight();
        if (Bar)
            return 1;
        var top = ScrollY - Style.PaddingY;
        var lo = 1;
        var hi = Doc.Lines.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if ((Row(mid) + 1) * lh > top)
                hi = mid;
            else
                lo = mid + 1;
        }
        return lo;
    }

    // The last line whose top edge is above the bottom edge of the view
    public int LastVisibleLine()
    {
        var lh = LineHeight();
        if (Bar)
            return 1;
        return LastLineAbove(ScrollY + SizeY - Style.PaddingY, lh);
    }

    // The last line whose top edge (from the top of the content, including empty lines) is at most bottom (0 if none)
    int LastLineAbove(float bottom, float lh)
    {
        var lo = 0;
        var hi = Doc.Lines.Count;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Row(mid) * lh <= bottom)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    // Width (px) of line line before column col. The newline at the end of the line counts as 1 column
    public float ColXOffset(int line, int col)
    {
        if (line < 1 || line > Doc.Lines.Count)
            return 0;
        var s = Doc.Lines[line - 1];
        var n = Str.Len(s);
        if (col <= 1)
            return 0;
        var cells = Str.Cells(Str.Slice(s, 0, Math.Min(col - 1, n)), Style.TabSize);
        // A column in the middle of a character counts that character
        var at = Math.Min(col - 1, n);
        while (at > 0 && at < n && Str.IsCont(Str.Byte(s, at)))
        {
            at++;
            cells = Str.Cells(Str.Slice(s, 0, at), Style.TabSize);
        }
        if (col - 1 > n)
            cells += 1;
        return cells * DrawList.CellW;
    }

    // Column at x px on line line (the next column if right of the middle of the character)
    public int XOffsetCol(int line, float x)
    {
        var s = Doc.Lines[line - 1];
        var u = new TinySystem.Text.Utf8Bytes(s);
        var xoffset = 0.0f;
        var i = 1;
        var at = 0;
        while (at < u.Length)
        {
            int b = u[at];
            var len = Math.Max(1, Math.Min(Str.RuneBytes(b), u.Length - at));
            var w = (b == 9 ? Style.TabSize : b < 0x80 ? 1 : LiderCore.Utf8.CellWidth(Str.Decode(u, at))) * DrawList.CellW;
            if (xoffset + w >= x)
                return x <= xoffset + w / 2 ? i : i + len;
            xoffset += w;
            i += len;
            at += len;
        }
        // 1 column for the newline
        var nw = DrawList.CellW;
        if (xoffset + nw >= x)
            return x <= xoffset + nw / 2 ? i : i + 1;
        return i;
    }

    public void ResolvePixelPosition(float x, float y, out int line, out int col)
    {
        // The last line whose top edge is at most y (within empty lines, the line above)
        var l = LastLineAbove(y - ContentOffsetY() - Style.PaddingY, LineHeight());
        l = Math.Max(1, Math.Min(l, Doc.Lines.Count));
        line = l;
        col = XOffsetCol(l, x - LineX());
    }

    public void MoveToLineOffset(int line, int col, int offset, out int l, out int c)
    {
        if (xoLine != line || xoCol != col)
            xoOffset = ColXOffset(line, col);
        xoLine = line + offset;
        xoCol = XOffsetCol(line + offset, xoOffset);
        l = xoLine;
        c = xoCol;
    }

    public void ScrollToLine(int line, bool ignoreIfVisible, bool instant)
    {
        if (Bar)
            return;
        var min = FirstVisibleLine();
        var max = LastVisibleLine();
        if (!(ignoreIfVisible && line > min && line < max))
        {
            var y = LineY(line) - ContentOffsetY();
            ToY = Math.Max(0, y - SizeY / 2);
            if (instant)
                ScrollY = ToY;
        }
    }

    public void ScrollToMakeVisible(int line, int col)
    {
        if (Bar)
            return;
        var ly = LineY(line) - ContentOffsetY();
        var lh = LineHeight();
        var overscroll = Math.Min(lh * 2, SizeY);
        ToY = Math.Max(Math.Min(ToY, ly - lh), ly - SizeY + overscroll);
        var gw = GutterWidth();
        var xoffset = ColXOffset(line, col);
        var xmargin = 3 * DrawList.CellW;
        var xsup = xoffset + gw + xmargin;
        var xinf = xoffset - xmargin;
        var scrollW = ScrollableSize() > SizeY ? Style.ScrollbarSize : 0;
        var sizeX = Math.Max(0, SizeX - scrollW);
        // Lite XL keeps the horizontal scroll as long as the cursor is visible, but then the start of a line stays cut off even after leaving a line longer than the window, so
        // it returns to the left edge if the cursor is visible without scrolling
        if (xsup <= sizeX)
            ToX = 0;
        else if (xsup > ScrollX + sizeX)
            ToX = xsup - sizeX;
        else if (xinf < ScrollX)
            ToX = Math.Max(0, xinf);
    }

    public void ClampScroll()
    {
        if (Bar)
        {
            ToX = 0;
            ToY = 0;
            return;
        }
        ToY = Math.Max(Math.Min(ToY, ScrollableSize() - SizeY), 0);
        ToX = Math.Max(ToX, 0);
    }

    // When the selection changes, scrolls so the cursor is visible, and moves the scroll to the destination
    public void Update()
    {
        Doc.GetSelection(false, out var l1, out var c1, out var l2, out var c2);
        if ((l1 != lastLine1 || c1 != lastCol1 || l2 != lastLine2 || c2 != lastCol2) && SizeX > 0)
        {
            if (EditorCore.IsActive(this) && !EditorCore.ImeEditing)
                ScrollToMakeVisible(l1, c1);
            lastLine1 = l1;
            lastCol1 = c1;
            lastLine2 = l2;
            lastCol2 = c2;
        }
        UpdateImeLocation();
        ClampScroll();
        if (ScrollX != ToX || ScrollY != ToY)
            EditorCore.Redraw = true;
        ScrollX = ToX;
        ScrollY = ToY;
    }

    // Does not scroll to follow the cursor when everything is selected
    public void ForgetSelection()
    {
        Doc.GetSelection(false, out var l1, out var c1, out var l2, out var c2);
        lastLine1 = l1;
        lastCol1 = c1;
        lastLine2 = l2;
        lastCol2 = c2;
    }

    // Uses the range of the composing text as the position for the IME candidates
    public void UpdateImeLocation()
    {
        if (!ImeStatus)
            return;
        Doc.GetSelection(true, out var line1, out var col1, out var line2, out var col2);
        var x = LineX();
        var y = LineY(line1);
        var col = Math.Min(col1, col2);
        float x1;
        float x2;
        if (ImeSize > 0)
        {
            var from = col + ImeFrom;
            x1 = ColXOffset(line1, from);
            x2 = ColXOffset(line1, from + ImeSize);
        }
        else
        {
            x1 = ColXOffset(line1, col1);
            x2 = ColXOffset(line2, col2);
        }
        Ime.SetLocation(x + x1, y, x2 - x1, LineHeight());
    }

    public void OnImeTextEditing(string text, int start, int length)
    {
        Doc.Quiet = true;
        Doc.ImeTextEditing(text);
        Doc.Quiet = false;
        ImeStatus = text != "";
        ImeFrom = start;
        ImeSize = length;
        Doc.GetSelection(true, out var line1, out var col1, out _, out var col2);
        var col = Math.Min(col1, col2);
        UpdateImeLocation();
        ScrollToMakeVisible(line1, col + start);
    }

    public void OnMouseMoved(float x, float y)
    {
        var gw = GutterWidth();
        HoveringGutter = gw > 0 && x >= PosX && x <= PosX + gw;
        if (!MouseSelecting)
            return;
        ResolvePixelPosition(x, y, out var l1, out var c1);
        var l2 = msLine;
        var c2 = msCol;
        if (EditorCore.Ctrl)
        {
            if (l1 > l2)
            {
                var t = l1;
                l1 = l2;
                l2 = t;
            }
            Doc.Selections = new List<int>();
            for (var i = l1; i <= l2; i++)
                Doc.SetSelections(i - l1 + 1, i, Math.Min(c1, Doc.LineSize(i)), i, Math.Min(c2, Doc.LineSize(i)));
            return;
        }
        if (msSnap != 0)
        {
            MouseSelection(msSnap, l1, c1, l2, c2, out var s1, out var sc1, out var s2, out var sc2);
            Doc.SetSelection(s1, sc1, s2, sc2);
            return;
        }
        Doc.SetSelection(l1, c1, l2, c2);
    }

    void MouseSelection(int snap, int line1, int col1, int line2, int col2, out int r1, out int rc1, out int r2, out int rc2)
    {
        var swap = line2 < line1 || (line2 == line1 && col2 <= col1);
        if (swap)
        {
            var l = line1;
            var c = col1;
            line1 = line2;
            col1 = col2;
            line2 = l;
            col2 = c;
        }
        if (snap == 2)
        {
            Doc.Translate(Move.StartOfWord, this, line1, col1, out var a, out var b);
            Doc.Translate(Move.EndOfWord, this, line2, col2, out var e, out var f);
            line1 = a;
            col1 = b;
            line2 = e;
            col2 = f;
        }
        else if (snap == 3)
        {
            col1 = 1;
            col2 = Str.Huge;
        }
        if (swap)
        {
            r1 = line2;
            rc1 = col2;
            r2 = line1;
            rc2 = col1;
            return;
        }
        r1 = line1;
        rc1 = col1;
        r2 = line2;
        rc2 = col2;
    }

    public void StartMouseSelection(int line, int col, int snap)
    {
        MouseSelecting = true;
        msLine = line;
        msCol = col;
        msSnap = snap;
    }

    // If the gutter was pressed, selects the line and returns true
    public bool OnMousePressed(float x, float y, int clicks)
    {
        if (!HoveringGutter)
            return false;
        ResolvePixelPosition(x, y, out var line, out _);
        if (EditorCore.Shift)
        {
            Doc.GetSelection(true, out var sline, out _, out var sline2, out _);
            if (line > sline)
                Doc.SetSelection(sline, 1, line, Doc.LineSize(line));
            else
                Doc.SetSelection(line, 1, sline2, Doc.LineSize(sline2));
        }
        else if (clicks == 1)
            Doc.SetSelection(line, 1, line, 1);
        else if (clicks == 2)
            Doc.SetSelection(line, 1, line, Doc.LineSize(line));
        return true;
    }

    public void OnMouseReleased()
    {
        MouseSelecting = false;
    }

    // Drawing ------------------------------------------------------------------

    public void Draw()
    {
        DrawList.Rect(PosX, PosY, SizeX, SizeY, Style.Background);
        var minline = FirstVisibleLine();
        var maxline = LastVisibleLine();
        var gw = GutterWidth();
        var gpad = GutterPadding();
        for (var i = minline; i <= maxline; i++)
            DrawLineGutter(i, PosX, LineY(i), gw - gpad);
        var x = LineX();
        DrawList.PushClip(PosX + gw, PosY, SizeX - gw, SizeY);
        for (var i = minline; i <= maxline; i++)
        {
            DrawList.Tag = i;
            DrawLineBody(i, x, LineY(i));
            DrawList.Tag = 0;
        }
        Doc.GetSelection(false, out var tagLine, out _, out _, out _);
        DrawList.Tag = tagLine;
        DrawOverlay(minline, maxline);
        DrawList.Tag = 0;
        DrawList.PopClip();
        DrawScrollbar();
    }

    void DrawLineGutter(int line, float x, float y, float width)
    {
        var lh = LineHeight();
        if (Bar)
        {
            var yoffset = (lh - DrawList.FontHeight()) / 2;
            DrawList.PushClip(PosX, PosY, BarGutter, SizeY);
            DrawList.DrawText(Font(), Label, x + Style.PaddingX, y + yoffset, Style.Text);
            DrawList.PopClip();
            return;
        }
        if (!LineNumbers)
            return;
        var color = Style.LineNumber;
        for (var idx = 1; idx <= Doc.SelectionCount(); idx++)
        {
            Doc.GetSelectionIdx(idx, true, out var l1, out _, out var l2, out _, out _);
            if (line >= l1 && line <= l2)
            {
                color = Style.LineNumber2;
                break;
            }
        }
        DrawList.DrawAligned(Font(), color, line.ToString(), 2, x + Style.PaddingX, y, width, lh);
    }

    void DrawLineBody(int line, float x, float y)
    {
        var lh = LineHeight();
        if (!Bar && EditorCore.IsActive(this))
        {
            for (var idx = 1; idx <= Doc.SelectionCount(); idx++)
            {
                Doc.GetSelectionIdx(idx, false, out var l1, out _, out _, out _, out _);
                if (l1 == line)
                {
                    DrawList.Rect(x + ScrollX, y, SizeX, lh, Style.LineHighlight);
                    break;
                }
            }
        }
        for (var idx = 1; idx <= Doc.SelectionCount(); idx++)
        {
            Doc.GetSelectionIdx(idx, true, out var l1, out var c1, out var l2, out var c2, out _);
            if (line < l1 || line > l2)
                continue;
            if (l1 != line)
                c1 = 1;
            if (l2 != line)
                c2 = Doc.LineSize(line) + 1;
            var x1 = x + ColXOffset(line, c1);
            var x2 = x + ColXOffset(line, c2);
            if (x1 != x2)
                DrawList.Rect(x1, y, x2 - x1, lh, Style.Selection);
        }
        DrawLineText(line, x, y);
    }

    void DrawLineText(int line, float x, float y)
    {
        var tx = x;
        var ty = y + (LineHeight() - DrawList.FontHeight()) / 2;
        var tokens = Doc.Highlighter.GetLine(line);
        for (var i = 0; i < tokens.Types.Count; i++)
        {
            tx = DrawList.DrawText(Font(), tokens.Texts[i], tx, ty, Style.Syntax(tokens.Types[i]));
            if (tx > PosX + SizeX)
                break;
        }
    }

    void DrawCaret(float x, float y)
    {
        // The cursor of a window view is not turned into a command; only its position is recorded (lider draws it outside the texture and slides it)
        if (!Bar)
        {
            Carets.Add(x);
            Carets.Add(y);
            return;
        }
        DrawList.Rect(x, y, Style.CaretWidth, LineHeight(), Style.Caret);
    }

    void DrawOverlay(int minline, int maxline)
    {
        if (!EditorCore.IsActive(this))
            return;
        for (var idx = 1; idx <= Doc.SelectionCount(); idx++)
        {
            Doc.GetSelectionIdx(idx, false, out var l1, out var c1, out var l2, out var c2, out _);
            if (l1 < minline || l1 > maxline)
                continue;
            if (EditorCore.ImeEditing)
                DrawImeDecoration(l1, c1, l2, c2);
            else
                DrawCaret(ColX(l1, c1), LineY(l1));
        }
    }

    void DrawImeDecoration(int line1, int col1, int line2, int col2)
    {
        var x = LineX();
        var y = LineY(line1);
        var lh = LineHeight();
        var x1 = ColXOffset(line1, col1);
        var x2 = ColXOffset(line2, col2);
        DrawList.Rect(x + Math.Min(x1, x2), y + lh - 1, Math.Abs(x1 - x2), 1, Style.Text);
        var from = Math.Min(col1, col2) + ImeFrom;
        var to = from + ImeSize;
        x1 = ColXOffset(line1, from);
        if (from != to)
        {
            x2 = ColXOffset(line1, to);
            DrawList.Rect(x + Math.Min(x1, x2), y + lh - Style.CaretWidth, Math.Abs(x1 - x2), Style.CaretWidth, Style.Caret);
        }
        DrawCaret(x + x1, y);
    }

    void DrawScrollbar()
    {
        var sz = ScrollableSize();
        if (sz <= SizeY)
            return;
        var along = Math.Max(20, SizeY * SizeY / sz);
        var y = PosY + ScrollY * (SizeY - along) / (sz - SizeY);
        DrawList.Rect(PosX + SizeX - Style.ScrollbarSize, y, Style.ScrollbarSize, along, Style.Scrollbar);
    }
}
