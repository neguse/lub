// 負荷の数と、値の大きさの帯を描く。
// 帯は専用の幅を取らず、行の下地に、数と同じ右の端から、一番大きい値で幅いっぱいになる長さで塗る (縦に並べると大小が帯の長さで分かる)。
using System;
using Lub;
using Lubx;

namespace LiderCore;

public static class LoadBar
{
    // 数の欄の幅 (桁)
    public const int Cells = 6;

    // x から幅 w の中に、値の大きさの帯を、右の端から左へ伸ばして塗る。
    // 一番大きい値の 15% に満たない帯は描かない (数の横に細い線だけが残り、桁がずれて見える)
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
