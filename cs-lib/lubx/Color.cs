// Color of the implementation library lubx.
// static factory (rgb / hex) + read-only public fields.
// Many are created every frame, so it is a value type that needs no allocation (readonly struct).

using Lub;

namespace Lubx;

/// <summary>RGBA color (value type).
/// Each component is 0..1.
/// Create with Color.rgb / Color.hex.
/// </summary>
public readonly struct Color
{
    public readonly float R;
    public readonly float G;
    public readonly float B;
    public readonly float A;

    private Color(float r, float g, float b, float a)
    {
        this.R = r;
        this.G = g;
        this.B = b;
        this.A = a;
    }

    /// <summary>From components (a defaults to 1.0).
    /// </summary>
    public static Color Rgb(float r, float g, float b, float? a = null)
    {
        return new Color(r, g, b, a ?? 1.0f);
    }

    /// <summary>0xRRGGBB (a defaults to 1.0).
    /// Example: Color.hex(0xE85C5C).
    /// </summary>
    public static Color Hex(int rgb, float? a = null)
    {
        float r = ((rgb >> 16) & 0xFF) / 255.0f;
        float g = ((rgb >> 8) & 0xFF) / 255.0f;
        float b = (rgb & 0xFF) / 255.0f;
        return new Color(r, g, b, a ?? 1.0f);
    }
}
