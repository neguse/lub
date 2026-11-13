// 板と窓に共通の色と角の丸み。
// 地は夜の藍で、ゲームが透ける。琥珀は実行と値 (今動いているもの) だけに使う。focus の冷たい青はその対。
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
    // 窓の見出しの、出しているタブの下地
    public static readonly Color TabCurrent = Color.Rgb(1, 1, 1, 0.12f);
    public const float Corner = 0.012f;
    // 負荷の段 (LoadProfile.Level) の色。琥珀は実行と値に使うので、負荷は赤紫にする
    static readonly Color[] LoadInks = new Color[] { Muted, Color.Rgb(0.93f, 0.62f, 0.78f), Color.Rgb(1.0f, 0.36f, 0.56f) };

    public static Color Load(int level)
    {
        return LoadInks[level];
    }

    // 下地の板: 影、細い縁、面の順に重ねる。y は板の真ん中
    public static void Plate(Mat4 vp, Color surface, float angle, float y, float r, float w, float h)
    {
        Quad3d.Round(vp, Color.Rgb(0, 0, 0, 0.35f), angle, y - 0.006f, r + 0.002f, w + 0.03f, h + 0.03f, Corner + 0.015f, 0.015f);
        Quad3d.Round(vp, Hairline, angle, y, r + 0.001f, w + 0.003f, h + 0.003f, Corner + 0.0015f, 0);
        Quad3d.Round(vp, surface, angle, y, r, w, h, Corner, 0);
    }
}
