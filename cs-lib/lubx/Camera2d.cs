// Camera2d of the implementation library lubx.
// Coordinate return values use the stub's coordinate wire format (Vec2d).
// The multi-return of Gfx.size / Input.mouse_pos is received through out arguments.

using Lub;

namespace Lubx;

/// <summary>
/// Conversion between 2D world coordinates (arbitrary units, y up) and logical screen px (y down).
/// ppm is px per world unit.
/// (originX, originY) is the screen position of the world origin.
/// </summary>
public class Camera2d
{
    public float Ppm;
    public float OriginX;
    public float OriginY;
    public float LogicalW;
    public float LogicalH;

    public Camera2d(float logicalW, float logicalH, float ppm,
        float originX, float originY)
    {
        this.LogicalW = logicalW;
        this.LogicalH = logicalH;
        this.Ppm = ppm;
        this.OriginX = originX;
        this.OriginY = originY;
    }

    /// <summary>world x → viewport x</summary>
    public float Sx(float wx)
    {
        return OriginX + wx * Ppm;
    }

    /// <summary>world y → viewport y</summary>
    public float Sy(float wy)
    {
        return OriginY - wy * Ppm;
    }

    /// <summary>viewport x → world x</summary>
    public float Wx(float sxv)
    {
        return (sxv - OriginX) / Ppm;
    }

    /// <summary>viewport y → world y</summary>
    public float Wy(float syv)
    {
        return (OriginY - syv) / Ppm;
    }

    /// <summary>Mouse position in world coordinates.
    /// Includes the conversion from real window px → logical px.
    /// </summary>
    public Vec2d MouseWorld()
    {
        Gfx.Size(out var gw, out var gh);
        Input.MousePos(out var mx, out var my);
        var lx = mx * LogicalW / gw;
        var ly = my * LogicalH / gh;
        return new Vec2d { X = Wx(lx), Y = Wy(ly) };
    }
}
