// ウィンドウの見出しの帯: 名前と閉じる印。
// 中身が自分で見出しを描かないウィンドウ (Code はタブを並べるので PanelView が描く) に付ける。
using Lub;
using Lubx;
using LiderCore;

public class TitleView
{
    readonly string key;
    readonly SpriteBatch batch;
    readonly DeclareGap gap = new DeclareGap();
    public string Label = "";
    // focus があるか、閉じる印を指しているか (Lider が毎 frame 決める)
    public bool Focused;
    public bool CloseLit;
    // 帯の幅 (px)
    public int PxW = 1;
    public TextureRef? Texture;
    string drawn = "";

    public TitleView(string key)
    {
        this.key = key;
        batch = new SpriteBatch(key, 1, PanelView.TitlePx);
    }

    // 見出しの帯の中の横の位置 (0..1) が閉じる印の上か
    public bool OnClose(float u)
    {
        return u >= 1 - (float)PanelView.TitlePx / PxW;
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
        var state = Label + "\n" + Focused + CloseLit + PxW + "x" + density + text.Version;
        if (Texture == null || state == drawn)
            return;
        drawn = state;
        Gfx.BeginPass(new PassOpts { Target = Texture, ClearColor = new float[] { 0, 0, 0, 0 } });
        batch.Begin();
        var ink = Focused ? Theme.Ink : Theme.Muted;
        var y = (PanelView.TitlePx - text.LineH) * 0.5f;
        var mark = PanelView.TitlePx;
        // SpriteBatch は atlas を初めて使った順に描くので、指していなくても (透明に) 先に塗る
        batch.Rect(PxW - mark + 3, 3, mark - 6, mark - 6, Color.Rgb(0.9f, 0.3f, 0.35f, CloseLit ? 1 : 0));
        text.Draw(batch, Label, 4 + text.CellW, y, ink, 0, PxW - mark);
        text.Draw(batch, "×", PxW - (mark + text.CellW) * 0.5f, y, ink);
        batch.Flush();
        Gfx.EndPass();
    }
}
