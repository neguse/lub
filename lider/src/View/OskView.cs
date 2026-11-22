// On-screen keyboard image: lays out the keys and highlights the pointed key and the active modifier keys.
using Lub;
using Lubx;
using LiderCore;

public class OskView
{
    // Size of one key (width 1) (px)
    public const int UnitPx = 56;
    public const int RowPx = 50;
    public const int PxW = 15 * UnitPx;
    readonly SpriteBatch batch = new SpriteBatch("lider_osk", PxW, 6 * RowPx);
    readonly DeclareGap gap = new DeclareGap();
    string drawn = "";

    public static int PxH(Osk osk)
    {
        return osk.Rows.Count * RowPx;
    }

    // Declares the texture and redraws if the appearance changed (call outside the pass to the display)
    public void Prepare(Osk osk, MonoText text)
    {
        if (gap.Lapsed())
            drawn = "";
        var density = text.Density;
        osk.Picture = Assets.RenderTarget("lider_osk_tex", PxW * density, PxH(osk) * density, Gfx.PixelFormat.Rgba8);
        batch.Ensure();
        text.Glyphs.Ensure();
        var state = osk.Mods + " " + osk.HoverRow[0] + " " + osk.HoverAt[0] + " " + osk.HoverRow[1] + " " + osk.HoverAt[1] + " " + density + " " + text.Version;
        if (osk.Picture == null || state == drawn)
            return;
        drawn = state;
        Gfx.BeginPass(new PassOpts { Target = osk.Picture, ClearColor = new float[] { Theme.Surface.R, Theme.Surface.G, Theme.Surface.B, 1 } });
        batch.Begin();
        for (var r = 0; r < osk.Rows.Count; r++)
        {
            var left = 0.0f;
            for (var i = 0; i < osk.Rows[r].Count; i++)
            {
                var key = osk.Rows[r][i];
                var x = left * UnitPx;
                var y = r * RowPx;
                var w = key.W * UnitPx;
                var lit = osk.Lit(r, i);
                var held = key.Mod != 0 && (osk.Mods & key.Mod) != 0;
                var face = held ? Theme.Accent : lit ? Theme.Selected : Theme.TabCurrent;
                batch.Rect(x + 2, y + 2, w - 4, RowPx - 4, Color.Rgb(face.R, face.G, face.B, held || lit ? 1 : face.A));
                var label = osk.Label(key);
                var cells = Utf8.Cells(label, Utf8.Bytes(label));
                text.Draw(batch, label, x + (w - cells * text.CellW) * 0.5f, y + (RowPx - text.LineH) * 0.5f, held ? Theme.Surface : Theme.Ink);
                left += key.W;
            }
        }
        batch.Flush();
        Gfx.EndPass();
    }
}
