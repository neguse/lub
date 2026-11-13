// 検索 (Search) の板: 打った字と候補を 1 行ずつ出す。窓より手前に置く。
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

namespace LiderCore;

public class SearchView
{
    const int RowH = 26;
    const int Pad = 8;
    const int PxW = PanelView.BasePxW;
    const int PxH = (Search.MaxResults + 1) * RowH + Pad * 2;
    const float W = PanelView.BaseW;
    const float H = W * PxH / PxW;
    const float R = Space.R - 0.2f;

    readonly SpriteBatch batch = new SpriteBatch("lider_search", PxW, PxH);
    TextureRef? texture;
    string drawn = "";
    readonly DeclareGap gap = new DeclareGap();
    // 使う行の数 (打った字と候補)
    int rows = 1;

    // texture を宣言し、中身が変わっていれば描き直す (画面への pass の外で呼ぶ)
    public void Prepare(Search search, MonoText text)
    {
        batch.Ensure();
        text.Glyphs.Ensure();
        texture = Assets.RenderTarget("lider_search", PxW * text.Density, PxH * text.Density, Gfx.PixelFormat.Rgba8);
        if (gap.Lapsed())
            drawn = "";
        rows = search.Results.Count + 1;
        var key = search.Query + "\n" + search.Selected + "\n" + text.Version + "\n" + text.Density;
        foreach (var item in search.Results)
            key += item.Text + (item.Dirty ? " *" : "") + "\t" + item.Where + "\n";
        if (texture == null || key == drawn)
            return;
        drawn = key;
        Gfx.BeginPass(new PassOpts { Target = texture, ClearColor = new float[] { 0, 0, 0, 0 } });
        batch.Begin();
        // SpriteBatch は atlas を初めて使った順に描くので、文字より先に塗る
        batch.Rect(0, 0, 1, 1, Color.Rgb(0, 0, 0, 0));
        var ink = Theme.Ink;
        var dim = Theme.Muted;
        var top = (RowH - text.LineH) * 0.5f;
        text.Draw(batch, ">", Pad * 2, Pad + top, Theme.Accent);
        text.Draw(batch, search.Query + "_", Pad * 2 + text.CellW * 2, Pad + top, ink);
        for (var i = 0; i < search.Results.Count; i++)
        {
            var item = search.Results[i];
            var y = Pad + (i + 1) * RowH;
            if (i == search.Selected)
                batch.Rect(Pad, y + 1, PxW - Pad * 2, RowH - 2, Theme.Selected);
            text.Draw(batch, item.Dirty ? item.Text + " *" : item.Text, Pad * 2, y + top, ink, 0, PxW - Pad);
            var cells = Utf8.Cells(item.Where, Utf8.Bytes(item.Where));
            if (cells > 0)
                text.Draw(batch, item.Where, PxW - Pad * 2 - cells * text.CellW, y + top, dim);
        }
        batch.Flush();
        Gfx.EndPass();
    }

    // 今の行の数での高さ
    public float Height(float scale)
    {
        return H * ((float)(rows * RowH + Pad * 2) / PxH) * scale;
    }

    // ray が、上の端の真ん中を (angle, top) に置いた板に当たるか
    public bool Hit(Ray ray, float angle, float top, float scale)
    {
        var k = R / Space.R;
        var h = Height(scale);
        return Space.HitCurved(ray, angle, (top - h * 0.5f) * k, R, W * scale * k, h * k) != null;
    }

    // 上の端の真ん中を円筒の (angle, top) にして描く
    public void Draw(Mat4 vp, float angle, float top, float scale)
    {
        if (texture == null)
            return;
        var k = R / Space.R;
        var used = (float)(rows * RowH + Pad * 2) / PxH;
        var h = H * used * scale;
        Theme.Plate(vp, Theme.Surface, angle, (top - h * 0.5f) * k, R + 0.003f, W * scale * k, h * k);
        Quad3d.Curved(vp, texture, Color.Rgb(1, 1, 1), angle, (top - h * 0.5f) * k, R, W * scale * k, h * k, 0, used);
    }
}
