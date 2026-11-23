// Rect of the implementation library lubx.
// A plain class with public fields + a positional constructor.

using Lub;

namespace Lubx;

/// <summary>Rectangle (px, top-left origin).
/// Used for the SpriteBatch src (inside the atlas) and Renderer3d.Viewport (inside the screen).
/// </summary>
public readonly struct Rect
{
    public readonly int X;
    public readonly int Y;
    public readonly int W;
    public readonly int H;

    public Rect(int x, int y, int w, int h)
    {
        this.X = x;
        this.Y = y;
        this.W = w;
        this.H = h;
    }
}
