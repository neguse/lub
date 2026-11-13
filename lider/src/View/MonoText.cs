// 等幅の文字描画。
// lubx の Text (glyph atlas) で 1 文字ずつ描き、固定幅の桁に置く (font が等幅でなくても桁が揃う)。
// 全角の文字は 2 桁に置く。
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

namespace LiderCore;

public class MonoText
{
    public readonly Text Glyphs;
    // 桁と行の高さは論理 px。Glyphs は 1 論理 px を Density 画素として作る
    public readonly float CellW;
    public readonly float LineH;
    public readonly int Density;
    // 初めて描いた文字が増えるたびに上がる。
    // 文字の atlas は描いた frame の後に GPU へ届くので、それで描いた texture は次の frame に描き直す
    public int Version;
    // 描いた文字 (codepoint) と、その font の送り幅
    readonly Dictionary<int, float> seen = new Dictionary<int, float>();
    // 密度が 1 でないときに描く 1 文字の文字列 (codepoint → 文字列。毎 frame 作り直さない)
    readonly Dictionary<int, string> chars = new Dictionary<int, string>();

    public MonoText(string key, string ttfPath, float px, int? density = null)
    {
        Density = density ?? 1;
        // 漢字を含むコードでも足りるよう、atlas は 1024 px 四方 (15 px で 3000 字ほど)
        Glyphs = new Text(key, ttfPath, px * Density, 1024 * Density);
        // 数字の幅 (等幅でない font でも数字はふつう揃っている)
        CellW = Glyphs.Width("0") / Density;
        LineH = Glyphs.LineHeight / Density;
        // 印字できる ASCII は先に atlas に入れる (打った英数字や記号で、描いた texture を全部描き直さない)
        for (var cp = 33; cp < 127; cp++)
            seen[cp] = Glyphs.GlyphAdvance(cp) / Density;
    }

    // 画素の格子に揃える (文字の bitmap が線形補間でぼけないように)
    float Snap(float v)
    {
        return (float)Math.Floor(v * Density + 0.5f) / Density;
    }

    // (x, y) は行の左上。
    // 文字 (codepoint) 1 つを、全角なら 2 桁、ほかは 1 桁の真ん中に置く (Utf8.CellWidth)。
    // 桁の左の端が minX より左か、右の端が maxX より右の文字は描かない (切り抜き)
    public void Draw(SpriteBatch batch, string s, float x, float y, Color color, float minX = -1e9f, float maxX = 1e9f)
    {
        var baseline = y + Glyphs.Ascent / Density;
        var cell = 0;
        var at = 0;
        while (at < Utf8.Bytes(s))
        {
            // ASCII は 1 byte で 1 桁 (UTF-8 を読み解かない)
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
