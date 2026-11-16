// 見出しの帯: 左からタブと名前、右の端に印 (閉じる印など)。
// ウィンドウの見出しと、スクリーンの名前の札に使う。
using System.Collections.Generic;
using Lub;
using Lubx;
using LiderCore;

public class TitleView
{
    // 閉じる印
    public const string Close = "×";

    readonly string key;
    readonly SpriteBatch batch;
    readonly DeclareGap gap = new DeclareGap();
    // 並べるタブの名前と、出しているタブ (タブの無い帯は空)
    public List<string> Tabs = new List<string>();
    public int Tab;
    // タブの後ろに出す名前
    public string Label = "";
    // 右の端から左へ並べる印 (1 つが帯の高さの幅を取る)
    public List<string> Marks = new List<string>();
    // 指している印 (無ければ -1)
    public int Lit = -1;
    // focus があるか
    public bool Focused;
    // 帯の幅 (px)
    public int PxW = 1;
    public TextureRef? Texture;
    string drawn = "";
    // 最後に描いたタブの左端と幅 (px)
    readonly List<float> tabX = new List<float>();
    readonly List<float> tabW = new List<float>();

    public TitleView(string key)
    {
        this.key = key;
        batch = new SpriteBatch(key, 1, PanelView.TitlePx);
    }

    // 帯の中の横の位置 (0..1) にある印 (無ければ "")
    public string MarkAt(float u)
    {
        var i = MarkIndexAt(u);
        return i >= 0 ? Marks[i] : "";
    }

    public int MarkIndexAt(float u)
    {
        var fromRight = (1 - u) * PxW / PanelView.TitlePx;
        return fromRight >= 0 && fromRight < Marks.Count ? (int)fromRight : -1;
    }

    // mark の印の真ん中の、帯の中の横の位置 (0..1)
    public float MarkU(string mark)
    {
        return 1 - (Marks.IndexOf(mark) + 0.5f) * PanelView.TitlePx / PxW;
    }

    // i 番目のタブの左端 (px。描く前は 0)
    public float TabX(int i)
    {
        return i < tabX.Count ? tabX[i] : 0;
    }

    // 帯の中の横の位置 (0..1) にあるタブ (無ければ -1)
    public int TabAt(float u)
    {
        var x = u * PxW;
        for (var i = 0; i < tabX.Count; i++)
            if (x >= tabX[i] && x < tabX[i] + tabW[i])
                return i;
        return -1;
    }

    // texture を宣言し、内容が変わっていれば描き直す (画面への pass の外で呼ぶ)
    public void Prepare(MonoText text)
    {
        if (gap.Lapsed())
            drawn = "";
        var density = text.Density;
        batch.LogicalW = PxW;
        Texture = Assets.RenderTarget(key + "_tex", PxW * density, PanelView.TitlePx * density, Gfx.PixelFormat.Rgba8);
        batch.Ensure();
        text.Glyphs.Ensure();
        var state = Label + "\n" + string.Join("\n", Tabs) + "\n" + string.Join("\n", Marks) + "\n" + Tab + Focused + Lit + PxW + "x" + density + text.Version;
        if (Texture == null || state == drawn)
            return;
        drawn = state;
        Gfx.BeginPass(new PassOpts { Target = Texture, ClearColor = new float[] { 0, 0, 0, 0 } });
        batch.Begin();
        var ink = Focused ? Theme.Ink : Theme.Muted;
        var y = (PanelView.TitlePx - text.LineH) * 0.5f;
        var side = PanelView.TitlePx;
        var right = PxW - side * Marks.Count;
        // SpriteBatch は atlas を初めて使った順に描くので、指していなくても (透明に) 先に塗る
        batch.Rect(0, 0, 1, 1, Color.Rgb(0, 0, 0, 0));
        // 指している印の下地: 閉じる印は赤、ほかは強調の色
        if (Lit >= 0 && Lit < Marks.Count)
            batch.Rect(PxW - side * (Lit + 1) + 3, 3, side - 6, side - 6,
                Marks[Lit] == Close ? Color.Rgb(0.9f, 0.3f, 0.35f, 1) : Color.Rgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, 0.8f));
        // タブは名前を並べ、出しているタブだけ下地を明るくする。後ろに名前を続ける
        tabX.Clear();
        tabW.Clear();
        var x = 4.0f;
        for (var i = 0; i < Tabs.Count; i++)
        {
            var label = Tabs[i];
            var w = (Utf8.Cells(label, Utf8.Bytes(label)) + 2) * text.CellW;
            tabX.Add(x);
            tabW.Add(w);
            batch.Rect(x, 3, w, side - 6, i == Tab ? Theme.TabCurrent : Color.Rgb(0, 0, 0, 0));
            text.Draw(batch, label, x + text.CellW, y, i == Tab ? ink : Theme.Muted, 0, right);
            x += w + 2;
        }
        if (Label != "")
            text.Draw(batch, Label, x + text.CellW, y, ink, 0, right);
        for (var i = 0; i < Marks.Count; i++)
        {
            var cells = Utf8.Cells(Marks[i], Utf8.Bytes(Marks[i]));
            text.Draw(batch, Marks[i], PxW - side * (i + 0.5f) - cells * text.CellW * 0.5f, y, ink);
        }
        batch.Flush();
        Gfx.EndPass();
    }
}
