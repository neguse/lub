// Converts input positions to pixels of the game's texture.
// The game picture window can be placed on any screen (a plane on the display, a cylinder in space), so positions are received as "position within the picture (0 to 1)".
// When the saved and edited versions are side by side with matched input, both games receive the same position within the picture of the window the mouse is in.
using Lub;

namespace LiderCore;

public class InputMap
{
    // Texture size in pixels
    public int W = 1;
    public int H = 1;
    // Pointed position within the picture (top left is 0,0, bottom right is 1,1)
    float u;
    float v;
    // Texture pixels per 1 unit of screen coordinates (to convert mouse movement amounts)
    float scale = 1;

    // w and h are the texture size in pixels
    public void Set(int w, int h)
    {
        W = w;
        H = h;
    }

    // uv is the pointed position within the picture; screenW is the on-screen width of the pointed window's picture (in screen coordinates)
    public void Point(Vec2 uv, float screenW)
    {
        u = uv.X;
        v = uv.Y;
        scale = screenW > 0 ? W / screenW : 1;
    }

    public float X => u * W;
    public float Y => v * H;

    // The event with its position converted. Mouse movement amounts become pixels on the picture
    public EventData Event(EventData e)
    {
        var motion = e.Kind == EventKind.MouseMotion;
        return new EventData
        {
            Kind = e.Kind,
            Key = e.Key,
            Mods = e.Mods,
            Typed = e.Typed,
            Plain = e.Plain,
            Button = e.Button,
            X = X,
            Y = Y,
            Dx = motion ? e.Dx * scale : e.Dx,
            Dy = motion ? e.Dy * scale : e.Dy,
        };
    }
}
