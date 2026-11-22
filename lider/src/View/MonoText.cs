// Monospace text rendering.
// Draws one character at a time with lubx's Text (glyph atlas) and places it in fixed-width columns (columns line up even if the font is not monospace).
// Full-width characters take 2 columns.
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

namespace LiderCore;

public class MonoText
{
    public readonly Text Glyphs;
    // Column width and line height are in logical px. Glyphs are built with 1 logical px as Density pixels
    public readonly float CellW;
    public readonly float LineH;
    public readonly int Density;
    // Increases each time a character is drawn for the first time.
    // The glyph atlas reaches the GPU after the frame it was drawn in, so textures drawn with it are redrawn on the next frame
    public int Version;
    // Drawn characters (codepoints) and their advance widths in the font
    readonly Dictionary<int, float> seen = new Dictionary<int, float>();
    // One-character strings drawn when the density is not 1 (codepoint → string; not recreated every frame)
    readonly Dictionary<int, string> chars = new Dictionary<int, string>();

    public MonoText(string key, string ttfPath, float px, int? density = null)
    {
        Density = density ?? 1;
        // The atlas is 1024 px square so it is enough even for code containing kanji (about 3000 characters at 15 px)
        Glyphs = new Text(key, ttfPath, px * Density, 1024 * Density);
        // Width of digits (digits are usually uniform even in a non-monospace font)
        CellW = Glyphs.Width("0") / Density;
        // Line height is rounded to an integer number of logical px, independent of density (if the font's line height is fractional, line bands do not land on pixel boundaries and changed lines alone cannot be redrawn)
        LineH = (float)Math.Round(Glyphs.LineHeight / Density);
        // Printable ASCII is put in the atlas up front (so typed letters, digits, and symbols do not cause every drawn texture to be redrawn)
        for (var cp = 33; cp < 127; cp++)
            seen[cp] = Glyphs.GlyphAdvance(cp) / Density;
    }

    // Snap to the pixel grid (so glyph bitmaps are not blurred by linear interpolation)
    float Snap(float v)
    {
        return (float)Math.Floor(v * Density + 0.5f) / Density;
    }

    // (x, y) is the top left of the line.
    // Places one character (codepoint) in the middle of 2 columns if full-width, otherwise of 1 column (Utf8.CellWidth).
    // Characters whose column's left edge is left of minX or whose right edge is right of maxX are not drawn (clipping)
    public void Draw(SpriteBatch batch, string s, float x, float y, Color color, float minX = -1e9f, float maxX = 1e9f)
    {
        var baseline = y + Glyphs.Ascent / Density;
        var cell = 0;
        var at = 0;
        while (at < Utf8.Bytes(s))
        {
            // ASCII is 1 byte per column (no UTF-8 decoding)
            int cp = Utf8.At(s, at);
            var n = 1;
            var cells = 1;
            if (cp >= 128)
            {
                n = Utf8.RuneBytes(Utf8.At(s, at));
                cp = Utf8.Decode(s, at);
                cells = Utf8.CellWidth(cp);
            }
            var left = x + cell * CellW;
            if (cp != ' ' && left >= minX && left + cells * CellW <= maxX)
            {
                if (!seen.TryGetValue(cp, out var w))
                {
                    w = Glyphs.GlyphAdvance(cp) / Density;
                    seen[cp] = w;
                    Version++;
                }
                var gx = left + (cells * CellW - w) * 0.5f;
                if (Density == 1)
                    Glyphs.DrawGlyph(batch, cp, gx, baseline, color);
                else
                {
                    if (!chars.TryGetValue(cp, out var one))
                    {
                        one = Utf8.Sub(s, at, n);
                        chars[cp] = one;
                    }
                    Glyphs.Draw(batch, one, Snap(gx), Snap(baseline), color, 1.0f / Density);
                }
            }
            at += n;
            cell += cells;
        }
    }
}
