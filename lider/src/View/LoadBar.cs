// Draws load numbers and bars for the value size.
// The bar takes no width of its own: it is painted in the row background from the same right edge as the number, with a length that fills the width for the largest value (stacked vertically, the bar lengths show which is larger).
using System;
using Lub;
using Lubx;

namespace LiderCore;

public static class LoadBar
{
    // Width of the number column (characters)
    public const int Cells = 6;

    // Paints a bar for the value size within width w from x, growing from the right edge to the left.
    // Bars below 15% of the largest value are not drawn (only a thin line would remain next to the number, and the digits would look misaligned)
    public static void Fill(SpriteBatch b, int level, float value, float max, float x, float y, float w, float lh, float alpha)
    {
        var ratio = Math.Min(1, value / Math.Max(max, 0.0001f));
        if (ratio < 0.15f)
            return;
        var ink = Theme.Load(level);
        b.Rect(x + w - w * ratio, y + 1, w * ratio, lh - 2, Color.Rgb(ink.R, ink.G, ink.B, alpha));
    }

    public static void Right(SpriteBatch b, MonoText text, string s, float right, float y, Color ink)
    {
        text.Draw(b, s, right - Utf8.Cells(s, Utf8.Bytes(s)) * text.CellW, y, ink);
    }
}
