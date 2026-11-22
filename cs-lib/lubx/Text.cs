// Text in the implementation library lubx.
// The glyph bitmap (gb.Bytes) is a view, so its bytes are scanned with Get(src).
using System;
using System.Collections.Generic;
using Lub;

namespace Lubx;

/// <summary>One glyph slot in the atlas (internal).
/// u/v is the top-left px in the atlas; xoff/yoff is the drawing offset from the baseline origin.
/// </summary>
public class TextGlyph
{
    public int U;
    public int V;
    public int W;
    public int H;
    public int Xoff;
    public int Yoff;
    public float Advance;
    // Position in the atlas (U, V, W, H). Not created on every draw
    public Rect Src = new Rect(0, 0, 0, 0);
    // The atlas page it is on
    public int Page;
}

// One page of the Text atlas (the RGBA pixel array glyphs are packed into, and the position for the next one).
// Adding a glyph uploads only that page's pixel array again
public class TextPage
{
    public Atlas Atlas;
    public List<int> Pixels;
    public int PenX = 1;
    public int PenY = 1;
    public int RowH = 0;

    public TextPage(string key, int size)
    {
        Pixels = new List<int>();
        for (int i = 0; i < size * size * 4; i++)
            Pixels.Add(0);
        Atlas = Atlas.FromPixels(key, size, size, Pixels);
    }
}

/// <summary>Fixed-size dynamic glyph atlas + single-line text drawing.
/// Calls Font.Glyph on demand, packs only the used glyphs into the atlas (RGBA, white + coverage α), and draws them with SpriteBatch.
/// Codepoints missing from the font are silently skipped (for a fallback chain, the caller layers Text instances).
/// Coordinates are the same logical px with a top-left origin as SpriteBatch, and the y of draw is the baseline position.
/// No line wrapping or line-breaking rules yet.
/// </summary>
public class Text
{
    /// <summary>Pixels per 1em (given to the constructor).
    /// </summary>
    public float Px;

    /// <summary>From the baseline to the top (px, positive).
    /// Read-only.
    /// </summary>
    public float Ascent;

    /// <summary>From the baseline to the bottom (px, negative).
    /// Read-only.
    /// </summary>
    public float Descent;

    /// <summary>Line advance (px).
    /// Read-only.
    /// </summary>
    public float LineHeight;

    private string ttfPath;
    private string key;
    // The atlas is split into pages of up to PageSize per side (so adding a glyph uploads only 1 page)
    private const int PageSize = 256;
    private int pageSize;
    private int maxPages;
    private List<TextPage> pages = new List<TextPage>();
    private Dictionary<int, TextGlyph> glyphs =
        new Dictionary<int, TextGlyph>();
    private Dictionary<int, bool> missing = new Dictionary<int, bool>();

    /// <summary>ttfPath is the path of a font file readable with Io.LoadBytes (it must be ready at the time of the call).
    /// atlasSize defaults to 256 when omitted.
    /// </summary>
    public Text(string key, string ttfPath, float px, int? atlasSize = null)
    {
        this.ttfPath = ttfPath;
        this.key = key;
        this.Px = px;
        int size = atlasSize ?? 256;
        // Holds as many glyphs as an atlasSize square, split into pages of up to PageSize per side
        pageSize = Math.Min(size, PageSize);
        maxPages = (size / pageSize) * (size / pageSize);
        var m = Font.Metrics(Ttf());
        Ascent = m.Ascent * px;
        Descent = m.Descent * px;
        LineHeight = (m.Ascent - m.Descent + m.LineGap) * px;
        pages.Add(new TextPage(key, pageSize));
    }

    // The font bytes are a view valid for the frame, so they are fetched from the cache on each use
    private Bytes Ttf()
    {
        Io.LoadBytes(ttfPath, out var b, out _, out _, out _);
        return b!;
    }

    // Previous char → next char → kerning (in font units). Like glyphs, only pairs seen for the first time are looked up in the font
    // (the font data is a view valid for the frame and cannot be kept, and reloading it on every lookup would allocate)
    private readonly Dictionary<int, Dictionary<int, float>> kerns = new Dictionary<int, Dictionary<int, float>>();

    private float Kern(int prev, int cp)
    {
        if (!kerns.TryGetValue(prev, out var row))
        {
            row = new Dictionary<int, float>();
            kerns[prev] = row;
        }
        if (!row.TryGetValue(cp, out var k))
        {
            k = Font.Kern(Ttf(), prev, cp);
            row[cp] = k;
        }
        return k;
    }

    private TextGlyph? EnsureGlyph(int cp)
    {
        if (glyphs.TryGetValue(cp, out var cached))
            return cached;
        if (missing.ContainsKey(cp))
            return null;
        var gb = Font.Glyph(Ttf(), cp, Px);
        if (gb == null)
        {
            missing[cp] = true;
            return null;
        }
        int u = 0;
        int v = 0;
        var page = pages[pages.Count - 1];
        if (gb.Bytes != null && gb.W > 0 && gb.H > 0)
        {
            if (page.PenX + gb.W + 1 > pageSize)
            {
                page.PenX = 1;
                page.PenY += page.RowH + 1;
                page.RowH = 0;
            }
            if (page.PenY + gb.H + 1 > pageSize)
            {
                if (pages.Count >= maxPages || gb.W + 2 > pageSize || gb.H + 2 > pageSize)
                {
                    Console.WriteLine(
                        "lubx.Text: atlas full, glyph dropped: " + cp);
                    missing[cp] = true;
                    return null;
                }
                page = new TextPage(key + "_" + pages.Count, pageSize);
                pages.Add(page);
            }
            u = page.PenX;
            v = page.PenY;
            int src = 0;
            for (int row = 0; row < gb.H; row++)
            {
                int dst = ((v + row) * pageSize + u) * 4;
                for (int i = 0; i < gb.W; i++)
                {
                    int a = gb.Bytes!.Get(src);
                    src++;
                    page.Pixels[dst] = 255;
                    page.Pixels[dst + 1] = 255;
                    page.Pixels[dst + 2] = 255;
                    page.Pixels[dst + 3] = a;
                    dst += 4;
                }
            }
            page.PenX += gb.W + 1;
            if (gb.H > page.RowH)
                page.RowH = gb.H;
            page.Atlas.UpdatePixels(page.Pixels);
        }
        var g = new TextGlyph
        {
            U = u,
            V = v,
            W = gb.W,
            H = gb.H,
            Xoff = gb.Xoff,
            Yoff = gb.Yoff,
            Advance = gb.Advance,
            Src = new Rect(u, v, gb.W, gb.H),
            Page = pages.Count - 1,
        };
        glyphs[cp] = g;
        return g;
    }

    /// <summary>Drawn width of one line (px).
    /// scale defaults to 1.0 when omitted.
    /// </summary>
    public float Width(string s, float? scale = null)
    {
        var sum = 0.0f;
        var prev = -1;
        foreach (var r in s.EnumerateRunes())
        {
            var cp = r.Value;
            var g = EnsureGlyph(cp);
            if (g == null)
                continue;
            if (prev >= 0)
                sum += Kern(prev, cp) * Px;
            sum += g.Advance;
            prev = cp;
        }
        return sum * (scale ?? 1.0f);
    }

    /// <summary>Draws one line.
    /// (x, y) is the left end of the baseline (logical px, top-left origin).
    /// scale defaults to 1.0 when omitted.
    /// </summary>
    public void Draw(SpriteBatch batch, string s, float x, float y,
        Color? tint = null, float? scale = null)
    {
        float sc = scale ?? 1.0f;
        var pen = x;
        var prev = -1;
        foreach (var r in s.EnumerateRunes())
        {
            var cp = r.Value;
            var g = EnsureGlyph(cp);
            if (g == null)
                continue;
            if (prev >= 0)
                pen += Kern(prev, cp) * Px * sc;
            if (g.W > 0)
                batch.Quad(pages[g.Page].Atlas, g.Src,
                    pen + g.Xoff * sc, y + g.Yoff * sc, g.W * sc, g.H * sc,
                    tint);
            pen += g.Advance * sc;
            prev = cp;
        }
    }

    /// <summary>Draws a single codepoint without adding kerning.
    /// (x, y) is the left end of the baseline (logical px, top-left origin).
    /// Returns the advance (px). Characters missing from the font are not drawn and return 0.
    /// An entry point that lets callers laying out characters themselves (monospaced digits, etc.) skip splitting a string.
    /// </summary>
    public float DrawGlyph(SpriteBatch batch, int cp, float x, float y,
        Color? tint = null)
    {
        var g = EnsureGlyph(cp);
        if (g == null)
            return 0;
        if (g.W > 0)
            batch.Quad(pages[g.Page].Atlas, g.Src, x + g.Xoff, y + g.Yoff, g.W, g.H, tint);
        return g.Advance;
    }

    /// <summary>Declares the textures of the atlas pages again.
    /// lub discards textures whose declaration lapses, so call this every frame so that frames without drawing do not force an upload again on the next draw.
    /// </summary>
    public void Ensure()
    {
        foreach (var p in pages)
            p.Atlas.Ensure();
    }

    /// <summary>Advance of a single codepoint (px). 0 for characters missing from the font.
    /// </summary>
    public float GlyphAdvance(int cp)
    {
        var g = EnsureGlyph(cp);
        return g == null ? 0 : g.Advance;
    }
}
