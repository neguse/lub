// Grabs and moves windows, and stretches them with both hands.
// With one hand, the window follows the pointed point, keeping the offset between the grab point and the window center (a position on the screen surface).
// With both hands, the size changes by the ratio of the current distance between the hands to the distance when grabbed.
using System;
using Lub;

namespace LiderCore;

public class Grab
{
    public const float MinScale = 0.5f;
    public const float MaxScale = 3;
    // Distance to bring a grabbed window forward (meters)
    public const float LiftDistance = 0.08f;
    public Window? Window;
    float xOffset;
    float yOffset;
    // While grabbed with both hands
    bool two;
    float startDistance;
    float startScale;

    public bool Holding => Window != null;

    // Grabs window at (x, y) on the screen surface
    public void Start(Window window, float x, float y)
    {
        Window = window;
        two = false;
        xOffset = (window.Position != null ? window.Position.Value.X : x) - x;
        yOffset = (window.Position != null ? window.Position.Value.Y : 0) - y;
    }

    // The pointed point moved to (x, y) on the screen surface
    public void Move(float x, float y)
    {
        if (Window == null || two)
            return;
        // The size stays as the arrangement rule set it
        Window.Place(x + xOffset, y + yOffset);
    }

    // Changes the size of the grabbed window by dy wheel steps
    public void Zoom(float dy)
    {
        if (Window != null)
            Rescale(Window, Window.Scale * (float)Math.Pow(1.1f, dy));
    }

    // Places the window center at (x, y) (size at the current scale)
    static void Place(Window window, float x, float y)
    {
        window.Place(x, y);
        window.Size = window.UnscaledSize * window.Scale;
    }

    static void Rescale(Window window, float scale)
    {
        window.Scale = Math.Max(MinScale, Math.Min(MaxScale, scale));
        if (window.Position != null)
            Place(window, window.Position.Value.X, window.Position.Value.Y);
    }

    // The other hand grabbed too (hand positions are a and b)
    public void StartTwo(Vec3 a, Vec3 b)
    {
        if (Window == null)
            return;
        two = true;
        startDistance = Math.Max(0.01f, (a - b).Length());
        startScale = Window.Scale;
    }

    public void MoveTwo(Vec3 a, Vec3 b)
    {
        if (Window == null || !two)
            return;
        Rescale(Window, startScale * (a - b).Length() / startDistance);
    }

    // One hand released (with two hands, goes back to one hand and grabs again from the pointed point)
    public void ReleaseTwo(float x, float y)
    {
        if (Window == null)
            return;
        Start(Window, x, y);
    }

    public void Release()
    {
        Window = null;
        two = false;
    }
}
