// Title band: tabs and name from the left, marks at the right end (close mark etc.).
// Used for window titles and screen name tags.
using System.Collections.Generic;
using Lub;
using Lubx;
using LiderCore;

public class TitleView
{
    // Close mark
    public const string Close = "×";

    readonly string key;
    readonly SpriteBatch batch;
    readonly DeclareGap gap = new DeclareGap();
    // Names of the tabs to lay out, and the shown tab (empty for bands without tabs)
    public List<string> Tabs = new List<string>();
    public int Tab;
    // Name shown after the tabs
    public string Label = "";
    // Marks laid out from the right end to the left (each takes a width equal to the band height)
    public List<string> Marks = new List<string>();
    // Mark being pointed at (-1 if none)
    public int Lit = -1;
    // Whether it has focus
    public bool Focused;
    // Band width (px)
    public int PxW = 1;
    public TextureRef? Texture;
    string drawn = "";
    // Left ends and widths of the last drawn tabs (px)
    readonly List<float> tabX = new List<float>();
    readonly List<float> tabW = new List<float>();

    public TitleView(string key)
    {
        this.key = key;
        batch = new SpriteBatch(key, 1, PanelView.TitlePx);
    }

    // Mark at a horizontal position in the band (0..1) ("" if none)
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

    // Horizontal position in the band (0..1) of the middle of mark
    public float MarkU(string mark)
    {
        return 1 - (Marks.IndexOf(mark) + 0.5f) * PanelView.TitlePx / PxW;
    }

    // Left end of the i-th tab (px; 0 before drawing)
    public float TabX(int i)
    {
        return i < tabX.Count ? tabX[i] : 0;
    }

    // Tab at a horizontal position in the band (0..1) (-1 if none)
    public int TabAt(float u)
    {
        var x = u * PxW;
        for (var i = 0; i < tabX.Count; i++)
            if (x >= tabX[i] && x < tabX[i] + tabW[i])
                return i;
        return -1;
    }

    // Declares the texture and redraws if the content changed (call outside the pass to the display)
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
        // SpriteBatch draws in the order atlases are first used, so fill first (transparent) even when nothing is pointed at
        batch.Rect(0, 0, 1, 1, Color.Rgb(0, 0, 0, 0));
        // Background of the pointed mark: red for the close mark, the accent color for others
        if (Lit >= 0 && Lit < Marks.Count)
            batch.Rect(PxW - side * (Lit + 1) + 3, 3, side - 6, side - 6,
                Marks[Lit] == Close ? Color.Rgb(0.9f, 0.3f, 0.35f, 1) : Color.Rgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, 0.8f));
        // Tabs lay out their names, and only the shown tab gets a brighter background. The name follows after them
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
