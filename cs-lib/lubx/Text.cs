// 実装ライブラリ lubx の Text。
// utf8 / string 関数は stub の utf8 / @string class で呼ぶ (@string は
// using static で取り込み byte/len を裸で呼ぶ)。
// pixels は 0-based 添字 (dst) で List<int> に書く (tcs の indexer が +1 して
// Lua の 1-based に写る)。glyph bitmap (gb.Bytes) は view なので Get(src) で
// byte 走査する。
// デフォルト引数値は nullable + ?? で受ける (tcs は call site 展開しない)。
// trace 相当は Console.WriteLine (Lua の print)。
using System;
using System.Collections.Generic;
using static Lub;

/// <summary>atlas 上のグリフ 1 枠 (内部用)。
/// u/v は atlas 内の左上 px、xoff/yoff はベースライン原点からの描画オフセット。</summary>
public class TextGlyph
{
    public int U;
    public int V;
    public int W;
    public int H;
    public int Xoff;
    public int Yoff;
    public float Advance;
}

/// <summary>固定サイズの動的 glyph atlas + 1行テキスト描画。Font.font_glyph を
/// オンデマンドに呼び、使ったグリフだけを atlas (RGBA、白 + coverage α) に
/// 詰めて SpriteBatch で描く。フォントに無い codepoint は黙ってスキップされる
/// (フォールバックチェーンは呼び出し側で Text を重ねる)。
/// 座標は SpriteBatch と同じ論理 px・左上原点で、draw の y はベースライン位置。
/// 折返し・禁則はまだ無い (docs/roadmap.md)。</summary>
public class Text
{
    /// <summary>1em のピクセル数 (コンストラクタ指定)。</summary>
    public float Px;

    /// <summary>ベースラインから上端まで (px、正)。読み取り専用。</summary>
    public float Ascent;

    /// <summary>ベースラインから下端まで (px、負)。読み取り専用。</summary>
    public float Descent;

    /// <summary>行送り (px)。読み取り専用。</summary>
    public float LineHeight;

    private string ttfPath;
    private Atlas atlas;
    private int atlasW;
    private int atlasH;
    private List<int> pixels;
    private Dictionary<int, TextGlyph> glyphs =
        new Dictionary<int, TextGlyph>();
    private Dictionary<int, bool> missing = new Dictionary<int, bool>();
    private int penX = 1;
    private int penY = 1;
    private int rowH = 0;

    /// <summary>ttfPath は Io.LoadBytes で読める font file の path (呼び出しの
    /// 時点で ready であること)。atlasSize 省略で 256。</summary>
    public Text(string key, string ttfPath, float px, int? atlasSize = null)
    {
        this.ttfPath = ttfPath;
        this.Px = px;
        int size = atlasSize ?? 256;
        atlasW = size;
        atlasH = size;
        pixels = new List<int>();
        for (int i = 0; i < atlasW * atlasH * 4; i++)
            pixels.Add(0);
        var m = Font.Metrics(Ttf());
        Ascent = m.Ascent * px;
        Descent = m.Descent * px;
        LineHeight = (m.Ascent - m.Descent + m.LineGap) * px;
        atlas = Atlas.FromPixels(key, atlasW, atlasH, pixels);
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
        if (gb.Bytes != null && gb.W > 0 && gb.H > 0)
        {
            if (penX + gb.W + 1 > atlasW)
            {
                penX = 1;
                penY += rowH + 1;
                rowH = 0;
            }
            if (penY + gb.H + 1 > atlasH)
            {
                Console.WriteLine(
                    "lubx.Text: atlas full, glyph dropped: " + cp);
                missing[cp] = true;
                return null;
            }
            u = penX;
            v = penY;
            int src = 0;
            for (int row = 0; row < gb.H; row++)
            {
                int dst = ((v + row) * atlasW + u) * 4;
                for (int i = 0; i < gb.W; i++)
                {
                    int a = gb.Bytes!.Get(src);
                    src++;
                    pixels[dst] = 255;
                    pixels[dst + 1] = 255;
                    pixels[dst + 2] = 255;
                    pixels[dst + 3] = a;
                    dst += 4;
                }
            }
            penX += gb.W + 1;
            if (gb.H > rowH)
                rowH = gb.H;
            atlas.UpdatePixels(pixels);
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
        };
        glyphs[cp] = g;
        return g;
    }

    /// <summary>1行の描画幅 (px)。scale 省略で 1.0。</summary>
    public float Width(string s, float? scale = null)
    {
        var sum = 0.0f;
        var prev = -1;
        EachCodepoint(s, cp =>
        {
            var g = EnsureGlyph(cp);
            if (g == null)
                return;
            if (prev >= 0)
                sum += Font.Kern(Ttf(), prev, cp) * Px;
            sum += g.Advance;
            prev = cp;
        });
        return sum * (scale ?? 1.0f);
    }

    /// <summary>1行描く。(x, y) はベースライン左端 (論理 px、左上原点)。
    /// scale 省略で 1.0。</summary>
    public void Draw(SpriteBatch batch, string s, float x, float y,
        Color? tint = null, float? scale = null)
    {
        float sc = scale ?? 1.0f;
        var pen = x;
        var prev = -1;
        EachCodepoint(s, cp =>
        {
            var g = EnsureGlyph(cp);
            if (g == null)
                return;
            if (prev >= 0)
                pen += Font.Kern(Ttf(), prev, cp) * Px * sc;
            if (g.W > 0)
                batch.Quad(atlas, new Rect(g.U, g.V, g.W, g.H),
                    pen + g.Xoff * sc, y + g.Yoff * sc, g.W * sc, g.H * sc,
                    tint);
            pen += g.Advance * sc;
            prev = cp;
        });
    }
}
