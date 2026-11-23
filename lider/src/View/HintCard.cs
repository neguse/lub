// Tag for operation hints (Hints). At the bottom right of the pointed point, shows one line per button: the button image and what pressing it does.
// Button images are drawn once at startup from distance functions into pixels and made into an atlas (each image is a square of Cell pixels).
// Faded while the pointed place moves, and solid when it stops (so it does not hide too much of the code being read).
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
    // Pixel density is the same as the code panels (PanelView)
    const float PerPx = PanelView.BaseW / PanelView.BasePxW;

    readonly SpriteBatch batch = new SpriteBatch("lider_hints", 1, 1);
    readonly Atlas icons = Atlas.FromPixels("lider_hint_icons", Cell * 7, Cell, IconPixels(), 1);
    string drawn = "";
    readonly DeclareGap gap = new DeclareGap();
    int rows;
    // Tag size (px). Decided by the number of hint lines shown and the longest line
    int pxW = 1;
    int pxH = 1;
    // Image size (px). Grown only when the tag no longer fits (recreating the image every time the hints change would make that frame longer)
    int texW = Grain;
    int texH = Grain;
    const int Grain = 128;
    public TextureRef? Texture;
    // Angle and height of the middle of the tag's text area (decided by the placer)
    public float Angle;
    public float Y;
    public bool Shown;

    // Declares the texture and redraws if the hints changed (call outside the pass to the display)
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
        // Growing the image recreates the texture, so redraw
        var key = texW + "x" + texH + "\n";
        foreach (var h in hints)
            key += (int)h.Icon + h.Key + "\t" + h.Label + "\n";
        if (Texture == null || key == drawn)
            return;
        drawn = key;
        rows = hints.Count;
        Render(hints, text);
    }

    // Draws on the screen surface (radius Space.R)
    float Radius()
    {
        return Space.R;
    }

    // Tag width and height (meters)
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
        // The image uses only the tag size from the top left, so shift the image-width board to the right so the tag's middle comes to Angle
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
        // Room for the hand letter (L or R) added at the bottom right
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
            // Paste the rounded-corner frame split into the left and right ends and the stretched middle
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

    // Pixels (RGBA) of the 7 images laid side by side. Drawn in white; color is applied when pasting
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

    // Intensity at (u, v) of image index (0 to 1, v points down)
    static float Coverage(int index, float u, float v)
    {
        switch (index)
        {
            case 0:
                return Fill(Circle(u, v, 0.5f, 0.5f, 0.44f));
            case 1:
                // Trigger seen from the side: a crescent plate for the finger hangs down from the top rim of the grip
                var grip = RoundRect(u, v, 0.55f, 0.16f, 0.4f, 0.09f, 0.09f);
                var blade = Math.Max(Circle(u, v, 0.62f, 0.3f, 0.5f), -Circle(u, v, 0.86f, 0.42f, 0.5f));
                if (v < 0.2f)
                    blade = 1;
                return Math.Max(Fill(grip), Fill(blade));
            case 2:
            case 3:
                // Stick head (ring and inner circle) and arrows for the tilt direction
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
                    // Fill the left button
                    var inside = RoundRect(u, v, 0.5f, 0.5f, 0.3f, 0.45f, 0.28f);
                    if (u < 0.5f && v < 0.44f && inside < 0)
                        return 1;
                    return lines;
                }
                return Math.Max(lines, Fill(RoundRect(u, v, 0.5f, 0.27f, 0.06f, 0.11f, 0.06f)));
        }
    }

    // Turns a distance (negative inside, in units of the image width) into intensity with the edge blurred by 1 pixel
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

    // Triangle with its tip at (tx, ty), pointing toward (dx, dy)
    static float Arrow(float u, float v, float tx, float ty, float dx, float dy)
    {
        // The base is 0.2 back from the tip, and the base width is 0.36
        var along = (u - tx) * dx + (v - ty) * dy;
        var side = Math.Abs((u - tx) * -dy + (v - ty) * dx);
        var back = -along - 0.2f;
        // Slanted sides: whether it sticks out too far sideways compared with the distance from the tip
        var slant = (side - (-along) * 0.9f) * 0.74f;
        return Math.Max(Math.Max(along, back), slant);
    }
}
