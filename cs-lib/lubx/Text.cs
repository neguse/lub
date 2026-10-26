// 実装ライブラリ lubx の Text。
// pixels は 0-based 添字 (dst) で List<int> に書く (tcs の indexer が +1 して Lua の 1-based に写る)。
// glyph bitmap (gb.Bytes) は view なので Get(src) で byte 走査する。
// デフォルト引数値は nullable + ?? で受ける (tcs は call site 展開しない)。
using System;
using System.Collections.Generic;
using Lub;

namespace Lubx;

/// <summary>atlas 上のグリフ 1 枠 (内部用)。
/// u/v は atlas 内の左上 px、xoff/yoff はベースライン原点からの描画オフセット。
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
    // atlas の中の位置 (U, V, W, H)。描くたびに作らない
    public Rect Src = new Rect(0, 0, 0, 0);
    // 載っている atlas の page
    public int Page;
}

// Text の atlas の 1 page (glyph を詰める RGBA の pixel 列と、次に置く位置)。
// glyph を足すと、その page の pixel 列だけを upload し直す
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

/// <summary>固定サイズの動的 glyph atlas + 1行テキスト描画。
/// Font.Glyph をオンデマンドに呼び、使ったグリフだけを atlas (RGBA、白 + coverage α) に詰めて SpriteBatch で描く。
/// フォントに無い codepoint は黙ってスキップされる (フォールバックチェーンは呼び出し側で Text を重ねる)。
/// 座標は SpriteBatch と同じ論理 px・左上原点で、draw の y はベースライン位置。
/// 折返し・禁則はまだ無い (docs/roadmap.md)。
/// </summary>
public class Text
{
    /// <summary>1em のピクセル数 (コンストラクタ指定)。
    /// </summary>
    public float Px;

    /// <summary>ベースラインから上端まで (px、正)。
    /// 読み取り専用。
    /// </summary>
    public float Ascent;

    /// <summary>ベースラインから下端まで (px、負)。
    /// 読み取り専用。
    /// </summary>
    public float Descent;

    /// <summary>行送り (px)。
    /// 読み取り専用。
    /// </summary>
    public float LineHeight;

    private string ttfPath;
    private string key;
    // atlas は一辺 PageSize までの page に分ける (glyph を足したときの upload を page 1 枚分にする)
    private const int PageSize = 256;
    private int pageSize;
    private int maxPages;
    private List<TextPage> pages = new List<TextPage>();
    private Dictionary<int, TextGlyph> glyphs =
        new Dictionary<int, TextGlyph>();
    private Dictionary<int, bool> missing = new Dictionary<int, bool>();

    /// <summary>ttfPath は Io.LoadBytes で読める font file の path (呼び出しの時点で ready であること)。
    /// atlasSize 省略で 256。
    /// </summary>
    public Text(string key, string ttfPath, float px, int? atlasSize = null)
    {
        this.ttfPath = ttfPath;
        this.key = key;
        this.Px = px;
        int size = atlasSize ?? 256;
        // atlasSize 四方と同じだけの glyph を、一辺 PageSize までの page に分けて持つ
        pageSize = Math.Min(size, PageSize);
        maxPages = (size / pageSize) * (size / pageSize);
        var m = Font.Metrics(Ttf());
        Ascent = m.Ascent * px;
        Descent = m.Descent * px;
        LineHeight = (m.Ascent - m.Descent + m.LineGap) * px;
        pages.Add(new TextPage(key, pageSize));
    }

    // font の byte 列は frame 有効の view なので、使うたびに cache から引く
    private Bytes Ttf()
    {
        Io.LoadBytes(ttfPath, out var b, out _, out _, out _);
        return b!;
    }

    private static void EachCodepoint(string s, Action<int> f)
    {
        foreach (var r in s.EnumerateRunes())
            f(r.Value);
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

    /// <summary>1行の描画幅 (px)。
    /// scale 省略で 1.0。
    /// </summary>
    public float Width(string s, float? scale = null)
    {
        var sum = 0.0f;
        var prev = -1;
        Bytes? ttf = null;
        EachCodepoint(s, cp =>
        {
            var g = EnsureGlyph(cp);
            if (g == null)
                return;
            if (prev >= 0)
            {
                if (ttf == null)
                    ttf = Ttf();
                sum += Font.Kern(ttf, prev, cp) * Px;
            }
            sum += g.Advance;
            prev = cp;
        });
        return sum * (scale ?? 1.0f);
    }

    /// <summary>1行描く。
    /// (x, y) はベースライン左端 (論理 px、左上原点)。
    /// scale 省略で 1.0。
    /// </summary>
    public void Draw(SpriteBatch batch, string s, float x, float y,
        Color? tint = null, float? scale = null)
    {
        float sc = scale ?? 1.0f;
        var pen = x;
        var prev = -1;
        Bytes? ttf = null;
        EachCodepoint(s, cp =>
        {
            var g = EnsureGlyph(cp);
            if (g == null)
                return;
            if (prev >= 0)
            {
                if (ttf == null)
                    ttf = Ttf();
                pen += Font.Kern(ttf, prev, cp) * Px * sc;
            }
            if (g.W > 0)
                batch.Quad(pages[g.Page].Atlas, g.Src,
                    pen + g.Xoff * sc, y + g.Yoff * sc, g.W * sc, g.H * sc,
                    tint);
            pen += g.Advance * sc;
            prev = cp;
        });
    }

    /// <summary>codepoint 1 文字を、kerning を足さずに描く。
    /// (x, y) はベースライン左端 (論理 px、左上原点)。
    /// 返り値は送り幅 (px)。font に無い文字は描かずに 0。
    /// 文字を自分で並べる呼び出し側 (等幅の桁など) が、文字列の分解を省くための口。
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

    /// <summary>codepoint 1 文字の送り幅 (px)。font に無い文字は 0。
    /// </summary>
    public float GlyphAdvance(int cp)
    {
        var g = EnsureGlyph(cp);
        return g == null ? 0 : g.Advance;
    }
}
