// List of commands that drew a view (rect, text, clip). Read by lider on the side that draws into the window texture.
// Fonts are measured with the dimensions of lider's single monospace font: width is the sum per character, full-width characters take 2 columns, and tab takes Style.TabSize columns.
using System;
using System.Collections.Generic;

namespace LiderEditor;

public static class DrawList
{
    public const int KindRect = 1;
    public const int KindText = 2;
    public const int KindClip = 3;

    // Column width and line height (px)
    public static float CellW = 8;
    public static float LineH = 16;

    public static int Count;
    public static readonly List<int> Kind = new List<int>();
    public static readonly List<float> X = new List<float>();
    public static readonly List<float> Y = new List<float>();
    public static readonly List<float> W = new List<float>();
    public static readonly List<float> H = new List<float>();
    public static readonly List<int> Color = new List<int>();
    public static readonly List<string> Text = new List<string>();
    public static readonly List<int> Font = new List<int>();
    public static readonly List<int> Line = new List<int>();

    // Line number attached to commands (the line while drawing a text line, otherwise 0). -1 during Overlay
    public static int Tag;
    public static bool Overlay;

    // Stack of clip rectangles (a sequence of x, y, w, h)
    static readonly List<float> clips = new List<float>();

    public static void Clear(float w, float h)
    {
        Count = 0;
        Tag = 0;
        Overlay = false;
        clips.Clear();
        clips.Add(0);
        clips.Add(0);
        clips.Add(w);
        clips.Add(h);
    }

    public static float FontHeight()
    {
        return LineH;
    }

    public static float Width(string text)
    {
        return Str.Cells(text, Style.TabSize) * CellW;
    }

    static void Push(int kind, float x, float y, float w, float h, int color, string text, int font)
    {
        var i = Count;
        Count++;
        if (i == Kind.Count)
        {
            Kind.Add(kind);
            X.Add(x);
            Y.Add(y);
            W.Add(w);
            H.Add(h);
            Color.Add(color);
            Text.Add(text);
            Font.Add(font);
            Line.Add(0);
        }
        else
        {
            Kind[i] = kind;
            X[i] = x;
            Y[i] = y;
            W[i] = w;
            H[i] = h;
            Color[i] = color;
            Text[i] = text;
            Font[i] = font;
        }
        Line[i] = Overlay ? -1 : Tag;
    }

    public static void SetClip(float x, float y, float w, float h)
    {
        Push(KindClip, x, y, w, h, 0, "", 0);
    }

    // Clips to the part overlapping the current rectangle
    public static void PushClip(float x, float y, float w, float h)
    {
        var n = clips.Count;
        var x2 = clips[n - 4];
        var y2 = clips[n - 3];
        var r = x + w;
        var b = y + h;
        var r2 = x2 + clips[n - 2];
        var b2 = y2 + clips[n - 1];
        x = Math.Max(x, x2);
        y = Math.Max(y, y2);
        b = Math.Min(b, b2);
        r = Math.Min(r, r2);
        clips.Add(x);
        clips.Add(y);
        clips.Add(r - x);
        clips.Add(b - y);
        SetClip(x, y, r - x, b - y);
    }

    public static void PopClip()
    {
        var n = clips.Count;
        if (n <= 4)
            return;
        for (var i = 0; i < 4; i++)
            clips.RemoveAt(clips.Count - 1);
        n = clips.Count;
        SetClip(clips[n - 4], clips[n - 3], clips[n - 2], clips[n - 1]);
    }

    public static void Rect(float x, float y, float w, float h, int color)
    {
        Push(KindRect, x, y, w, h, color, "", 0);
    }

    // Places text and returns the x of the right edge. Tabs are passed as spaces for their column count (lider only places characters on columns)
    public static float DrawText(int font, string text, float x, float y, int color)
    {
        var w = Width(text);
        Push(KindText, x, y, w, LineH, color, Str.ExpandTabs(text, Style.TabSize), font);
        return x + w;
    }

    // Places it aligned within w x h (align: 0 = left, 1 = center, 2 = right)
    public static float DrawAligned(int font, int color, string text, int align, float x, float y, float w, float h)
    {
        var tw = Width(text);
        if (align == 1)
            x = x + (w - tw) / 2;
        else if (align == 2)
            x = x + (w - tw);
        y = Round(y + (h - LineH) / 2);
        return DrawText(font, text, x, y, color);
    }

    public static float Round(float n)
    {
        return n >= 0 ? (float)Math.Floor(n + 0.5f) : (float)Math.Ceiling(n - 0.5f);
    }
}
