// Colors and corner rounding shared by boards and panels.
// The ground is night indigo, with the game showing through. Amber is used only for execution and values (what is running now). The cool blue of focus is its counterpart.
using Lub;
using Lubx;

namespace LiderCore;

public static class Theme
{
    public static readonly Color Ink = Color.Rgb(0.91f, 0.93f, 0.97f);
    public static readonly Color Muted = Color.Rgb(0.55f, 0.59f, 0.68f);
    public static readonly Color Accent = Color.Rgb(0.56f, 0.66f, 1.0f);
    public static readonly Color Selected = Color.Rgb(0.18f, 0.22f, 0.42f);
    public static readonly Color Value = Color.Rgb(1.0f, 0.78f, 0.42f);
    public static readonly Color Surface = Color.Rgb(0.055f, 0.07f, 0.15f, 0.96f);
    public static readonly Color Hairline = Color.Rgb(1, 1, 1, 0.09f);
    // Background of the shown tab in the panel title
    public static readonly Color TabCurrent = Color.Rgb(1, 1, 1, 0.12f);
    public const float Corner = 0.012f;
    // Colors for load levels (LoadProfile.Level). Amber is used for execution and values, so load is red-purple
    static readonly Color[] LoadInks = new Color[] { Muted, Color.Rgb(0.93f, 0.62f, 0.78f), Color.Rgb(1.0f, 0.36f, 0.56f) };

    public static Color Load(int level)
    {
        return LoadInks[level];
    }

    // Background board: layered as shadow, thin edge, then surface. y is the middle of the board
    public static void Plate(Mat4 vp, Color surface, float angle, float y, float r, float w, float h)
    {
        Quad3d.Round(vp, Color.Rgb(0, 0, 0, 0.35f), angle, y - 0.006f, r + 0.002f, w + 0.03f, h + 0.03f, Corner + 0.015f, 0.015f);
        Quad3d.Round(vp, Hairline, angle, y, r + 0.001f, w + 0.003f, h + 0.003f, Corner + 0.0015f, 0);
        Quad3d.Round(vp, surface, angle, y, r, w, h, Corner, 0);
    }
}
