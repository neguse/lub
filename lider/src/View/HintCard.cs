// 操作の案内 (Hints) の札。指している点の右下に、ボタンの絵と、押すと何が起きるかを 1 行ずつ出す。
// ボタンの絵は起動したときに 1 度だけ距離関数から画素に描き、atlas にする (1 つの絵が Cell 画素の正方形)。
// 指す所が動いている間は薄く、止まると濃くする (読んでいる code を隠しすぎないため)。
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

namespace LiderCore;

public class HintCard
{
    const int Cell = 64;
    const int RowH = 28;
    const int Pad = 6;
    const int IconS = 22;
    // 画素の細かさは code の窓 (PanelView) と同じ
    const float PerPx = PanelView.BaseW / PanelView.BasePxW;

    readonly SpriteBatch batch = new SpriteBatch("lider_hints", 1, 1);
    readonly Atlas icons = Atlas.FromPixels("lider_hint_icons", Cell * 7, Cell, IconPixels(), 1);
    string drawn = "";
    readonly DeclareGap gap = new DeclareGap();
    int rows;
    // 札の大きさ (px)。出す案内の行の数と、一番長い行で決める
    int pxW = 1;
    int pxH = 1;
    // 絵の大きさ (px)。札が収まらなくなったときだけ広げる (案内が変わるたびに絵を作り直すと、その frame が長くなる)
    int texW = Grain;
    int texH = Grain;
    const int Grain = 128;
    public TextureRef? Texture;
    // 札の字のある所の真ん中の角度と高さ (置く側が決める)
    public float Angle;
    public float Y;
    public bool Shown;

    // texture を宣言し、案内が変わっていれば描き直す (画面への pass の外で呼ぶ)
    public void Prepare(List<Hint> hints, MonoText text)
    {
        var width = 1f;
        foreach (var h in hints)
            width = Math.Max(width, Pad + IconWidth(h, text) + 8 + Utf8.Cells(h.Label, Utf8.Bytes(h.Label)) * text.CellW + Pad);
        pxW = (int)Math.Ceiling(width);
        pxH = hints.Count * RowH + 2 * Pad;
        texW = Math.Max(texW, (pxW + Grain - 1) / Grain * Grain);
        texH = Math.Max(texH, (pxH + Grain - 1) / Grain * Grain);
        batch.LogicalW = texW;
        batch.LogicalH = texH;
        batch.Ensure();
        Texture = Assets.RenderTarget("lider_hints", texW, texH, Gfx.PixelFormat.Rgba8);
        if (gap.Lapsed())
            drawn = "";
        // 絵を広げると texture を作り直すので、描き直す
        var key = texW + "x" + texH + "\n";
        foreach (var h in hints)
            key += (int)h.Icon + h.Key + "\t" + h.Label + "\n";
        if (Texture == null || key == drawn)
            return;
        drawn = key;
        rows = hints.Count;
        Render(hints, text);
    }

    // スクリーンの面 (半径 Space.R) の上に描く
    float Radius()
    {
        return Space.R;
    }

    // 札の幅と高さ (メートル)
    public float UsedW()
    {
        return PerPx * pxW;
    }

    public float Height()
    {
        return PerPx * pxH;
    }

    public void Draw(Mat4 vp)
    {
        if (!Shown || Texture == null || rows == 0)
            return;
        Theme.Plate(vp, Color.Rgb(Theme.Surface.R, Theme.Surface.G, Theme.Surface.B, 0.92f), Angle, Y, Radius() + 0.003f, UsedW(), Height());
        // 絵は左上から札の大きさだけを使うので、札の真ん中が Angle に来るよう、絵の幅の板を右へずらす
        var wide = PerPx * texW;
        Quad3d.Curved(vp, Texture, Color.Rgb(1, 1, 1, 1), Angle + (wide - UsedW()) * 0.5f / Radius(), Y, Radius(), wide, Height(), 0, (float)pxH / texH);
    }

    void Render(List<Hint> hints, MonoText text)
    {
        Gfx.BeginPass(new PassOpts { Target = Texture, ClearColor = new float[] { 0, 0, 0, 0 } });
        batch.Begin();
        batch.Rect(0, 0, 1, 1, Color.Rgb(0, 0, 0, 0));
        var ink = Theme.Ink;
        for (var i = 0; i < hints.Count; i++)
        {
            var h = hints[i];
            var x = (float)Pad;
            var cy = Pad + i * RowH + RowH * 0.5f;
            var w = IconWidth(h, text);
            DrawIcon(h, x, cy, w, text);
            text.Draw(batch, h.Label, x + w + 8, cy - text.LineH * 0.5f, ink, 0, pxW);
        }
        batch.Flush();
        Gfx.EndPass();
    }

    float IconWidth(Hint h, MonoText text)
    {
        if (h.Icon == HintIcon.None)
            return -8;
        if (h.Icon == HintIcon.KeyCap)
            return Math.Max(IconS, Utf8.Cells(h.Key, Utf8.Bytes(h.Key)) * text.CellW + 12);
        // 手の字 (L か R) を右下に添える分
        if (h.Icon != HintIcon.Face && h.Key != "")
            return IconS + text.CellW;
        return IconS;
    }

    void DrawIcon(Hint h, float x, float cy, float w, MonoText text)
    {
        if (h.Icon == HintIcon.None)
            return;
        var y = cy - IconS * 0.5f;
        var light = Color.Rgb(0.93f, 0.95f, 1.0f, 1);
        var index = h.Icon == HintIcon.Face ? 0 : h.Icon == HintIcon.Trigger ? 1 : h.Icon == HintIcon.StickUpDown ? 2
            : h.Icon == HintIcon.StickLeftRight ? 3 : h.Icon == HintIcon.KeyCap ? 4 : h.Icon == HintIcon.MouseLeft ? 5 : 6;
        if (h.Icon == HintIcon.KeyCap)
        {
            // 角の丸い枠を左右の端と、伸ばす真ん中に分けて貼る
            var edge = IconS * 0.4f;
            var src = (int)(Cell * 0.4f);
            batch.Quad(icons, new Rect(index * Cell, 0, src, Cell), x, y, edge, IconS, light);
            batch.Quad(icons, new Rect(index * Cell + src, 0, Cell - 2 * src, Cell), x + edge, y, w - 2 * edge, IconS, light);
            batch.Quad(icons, new Rect(index * Cell + Cell - src, 0, src, Cell), x + w - edge, y, edge, IconS, light);
            text.Draw(batch, h.Key, x + (w - Utf8.Cells(h.Key, Utf8.Bytes(h.Key)) * text.CellW) * 0.5f, cy - text.LineH * 0.5f, light, 0, pxW);
            return;
        }
        batch.Quad(icons, new Rect(index * Cell, 0, Cell, Cell), x, y, IconS, IconS, light);
        if (h.Icon == HintIcon.Face)
            text.Draw(batch, h.Key, x + (IconS - text.CellW) * 0.5f, cy - text.LineH * 0.5f, Color.Rgb(0.05f, 0.06f, 0.1f, 1), 0, pxW);
        else if (h.Key != "")
            text.Draw(batch, h.Key, x + IconS, cy - text.LineH * 0.2f, Color.Rgb(0.6f, 0.75f, 1.0f, 1), 0, pxW);
    }

    // 7 つの絵を横に並べた画素 (RGBA)。白で描き、色は貼るときに付ける
    static List<int> IconPixels()
    {
        var px = new List<int>();
        for (var y = 0; y < Cell; y++)
        {
            for (var x = 0; x < Cell * 7; x++)
            {
                var u = (x % Cell + 0.5f) / Cell;
                var v = (y + 0.5f) / Cell;
                var a = Coverage(x / Cell, u, v);
                px.Add(255);
                px.Add(255);
                px.Add(255);
                px.Add((int)(Math.Max(0, Math.Min(1, a)) * 255));
            }
        }
        return px;
    }

    // 絵 index の (u, v) (0 から 1、下向きが v) の濃さ
    static float Coverage(int index, float u, float v)
    {
        switch (index)
        {
            case 0:
                return Fill(Circle(u, v, 0.5f, 0.5f, 0.44f));
            case 1:
                // trigger を横から見た形: 握りの上の縁から、指をかける三日月の板が下がる
                var grip = RoundRect(u, v, 0.55f, 0.16f, 0.4f, 0.09f, 0.09f);
                var blade = Math.Max(Circle(u, v, 0.62f, 0.3f, 0.5f), -Circle(u, v, 0.86f, 0.42f, 0.5f));
                if (v < 0.2f)
                    blade = 1;
                return Math.Max(Fill(grip), Fill(blade));
            case 2:
            case 3:
                // stick の頭 (輪と中の丸) と、倒す向きの矢印
                var stick = Math.Max(Fill(Math.Abs(Circle(u, v, 0.5f, 0.5f, 0.2f)) - 0.035f), Fill(Circle(u, v, 0.5f, 0.5f, 0.1f)));
                if (index == 2)
                    return Math.Max(stick, Math.Max(Fill(Arrow(u, v, 0.5f, 0.02f, 0, -1)), Fill(Arrow(u, v, 0.5f, 0.98f, 0, 1))));
                return Math.Max(stick, Math.Max(Fill(Arrow(u, v, 0.02f, 0.5f, -1, 0)), Fill(Arrow(u, v, 0.98f, 0.5f, 1, 0))));
            case 4:
                return Fill(Math.Abs(RoundRect(u, v, 0.5f, 0.5f, 0.45f, 0.45f, 0.2f)) - 0.035f);
            default:
                var body = Fill(Math.Abs(RoundRect(u, v, 0.5f, 0.5f, 0.3f, 0.45f, 0.28f)) - 0.035f);
                var split = Math.Max(Fill(RoundRect(u, v, 0.5f, 0.25f, 0.018f, 0.2f, 0)), Fill(RoundRect(u, v, 0.5f, 0.44f, 0.3f, 0.018f, 0)));
                var lines = Math.Max(body, split);
                if (index == 5)
                {
                    // 左のボタンを塗る
                    var inside = RoundRect(u, v, 0.5f, 0.5f, 0.3f, 0.45f, 0.28f);
                    if (u < 0.5f && v < 0.44f && inside < 0)
                        return 1;
                    return lines;
                }
                return Math.Max(lines, Fill(RoundRect(u, v, 0.5f, 0.27f, 0.06f, 0.11f, 0.06f)));
        }
    }

    // 距離 (負が内側、単位は絵の幅) を、縁を 1 画素でぼかした濃さにする
    static float Fill(float d)
    {
        return Math.Max(0, Math.Min(1, 0.5f - d * Cell));
    }

    static float Circle(float u, float v, float cx, float cy, float r)
    {
        var dx = u - cx;
        var dy = v - cy;
        return (float)Math.Sqrt(dx * dx + dy * dy) - r;
    }

    static float RoundRect(float u, float v, float cx, float cy, float hw, float hh, float r)
    {
        var qx = Math.Abs(u - cx) - hw + r;
        var qy = Math.Abs(v - cy) - hh + r;
        var ox = Math.Max(qx, 0);
        var oy = Math.Max(qy, 0);
        return (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - r;
    }

    // 先が (tx, ty) で (dx, dy) を向いた三角
    static float Arrow(float u, float v, float tx, float ty, float dx, float dy)
    {
        // 先から 0.2 戻った所を底にし、底の幅は 0.36
        var along = (u - tx) * dx + (v - ty) * dy;
        var side = Math.Abs((u - tx) * -dy + (v - ty) * dx);
        var back = -along - 0.2f;
        // 斜めの辺: 先からの距離に比べて横に出すぎていないか
        var slant = (side - (-along) * 0.9f) * 0.74f;
        return Math.Max(Math.Max(along, back), slant);
    }
}
