// Screen: a surface placed in space.
// Windows (Window) are laid out on it, and the arrangement rule (ILayout) decides where.
// The shape is a plane or part of a cylinder around a vertical axis through the viewpoint. It is anchored to the view or to the world.
// The position is the screen's center point in the anchor's coordinates (+X right, +Y up, +Z forward, in meters).
// The cylinder's radius of curvature is Space.R, and moving the screen does not change the curvature.
// When the center point is Space.R away from the anchor's vertical axis, the cylinder's axis passes through the anchor's origin.
// The center's angle and depth are given by that point's direction around the axis and its distance from the axis.
// Positions on the screen surface are in meters with the screen center as the origin, +X to the right (arc length on a cylinder) and +Y up.
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public enum ScreenShape
{
    Plane,
    Cylinder,
}

public enum ScreenAnchor
{
    // Anchored to the view (appears at the same place on the display even when the viewpoint moves)
    View,
    // Anchored to the world
    World,
}

public class Screen
{
    public readonly string Name;
    public ScreenShape Shape = ScreenShape.Plane;
    public ScreenAnchor Anchor = ScreenAnchor.View;
    // Center position (in the anchor's coordinates)
    public Vec3 Position = new Vec3(0, 0, 1);
    // Width and height (meters). Not used for screens sized by their contents (Strip)
    public Vec2 Size = new Vec2(1, 1);
    // Opacity (1 = opaque)
    public float Opacity = 1;
    // Whether it is shown (while hidden, its windows are not drawn and receive no input)
    public bool Shown = true;
    // Whether users can grab and move it, and change its anchor
    public bool Movable;
    public ILayout Layout = new Full();
    // Windows (in layout order)
    public readonly List<Window> Windows = new List<Window>();
    // Window shown by the single-window rule (Full) (the first one if null)
    public Window? Focus;
    // Window placed at the center of the surface by the single-row rule (Strip) (the first one if null or not on this screen)
    public Window? Origin;
    // Window being grabbed and moved (arrangement rules do not move it, and reorder based on its position)
    public Window? Held;

    public Screen(string name)
    {
        Name = name;
    }

    // Distance from the viewpoint. Screens further back are drawn first
    public float Distance => (float)Math.Sqrt(Position.X * Position.X + Position.Y * Position.Y + Position.Z * Position.Z);

    // Depth of the cylinder's center (distance from the vertical axis)
    public float Radius => (float)Math.Sqrt(Position.X * Position.X + Position.Z * Position.Z);

    // Angle of the cylinder's center (+Z is 0, right is positive)
    public float Angle => (float)Math.Atan2(Position.X, Position.Z);

    // Cylinder angle at x on the surface
    public float AngleAt(float x)
    {
        return Angle + x / Space.R;
    }

    // x on the surface at cylinder angle angle
    public float XAt(float angle)
    {
        return (angle - Angle) * Space.R;
    }

    // Depth range (meters)
    public const float MinDepth = 0.6f;
    public const float MaxDepth = 6;

    // Moves a cylinder screen along its surface by dx meters right and dy meters up (depth unchanged)
    public void Shift(float dx, float dy)
    {
        Place(Angle + dx / Space.R, Position.Y + dy, Radius);
    }

    // Sets a cylinder screen's depth to depth meters (direction and height unchanged)
    public void SetDepth(float depth)
    {
        Place(Angle, Position.Y, Math.Max(MinDepth, Math.Min(MaxDepth, depth)));
    }

    // Places the center point at angle angle around the axis, height y, and distance depth from the axis (does not go past directly behind)
    void Place(float angle, float y, float depth)
    {
        var a = Math.Max(-3, Math.Min(3, angle));
        Position = new Vec3(depth * (float)Math.Sin(a), y, depth * (float)Math.Cos(a));
    }

    // Lays out the windows (with the arrangement rule).
    // Stowed windows (Window.Away) are removed from the order before it is passed to the rule, and put back in their original places after layout
    public void Arrange()
    {
        var away = new List<Window>();
        var at = new List<int>();
        for (var i = 0; i < Windows.Count; i++)
            if (Windows[i].Away)
            {
                away.Add(Windows[i]);
                at.Add(i);
            }
        foreach (var window in away)
        {
            Windows.Remove(window);
            window.Position = null;
            window.ViewportRect = null;
            window.ContentRect = null;
        }
        Layout.Arrange(this);
        for (var i = 0; i < away.Count; i++)
            Windows.Insert(Math.Min(at[i], Windows.Count), away[i]);
    }

    // Keeping the current distance, sizes it to exactly cover a view with vertical field of view fov (degrees) and aspect ratio aspect, and places it straight ahead
    public void Fill(float fov, float aspect)
    {
        Position = new Vec3(0, 0, Position.Z);
        var height = 2 * Position.Z * (float)Math.Tan(fov * 0.5f * Math.PI / 180);
        Size = new Vec2(height * aspect, height);
    }

    // On a plane screen, lays out the windows and sets their rectangles on the display (ViewportRect).
    // origin is the display position where the center of the screen surface appears (display coordinates), and perM is the length 1 meter on the surface covers on the display.
    // It is a plane facing the viewer, so the rectangles are axis-aligned and snapped to pixels
    public void Project(Vec2 origin, float perM)
    {
        Arrange();
        foreach (var window in Windows)
        {
            if (window.Position == null)
            {
                window.ViewportRect = null;
                window.ContentRect = null;
                continue;
            }
            var at = window.Position.Value;
            var size = window.Size;
            var x0 = (float)Math.Round(origin.X + (at.X - size.X * 0.5f) * perM);
            var y0 = (float)Math.Round(origin.Y - (at.Y + size.Y * 0.5f) * perM);
            var x1 = (float)Math.Round(origin.X + (at.X + size.X * 0.5f) * perM);
            var y1 = (float)Math.Round(origin.Y - (at.Y - size.Y * 0.5f) * perM);
            window.ViewportRect = new PixelRect(x0, y0, x1 - x0, y1 - y0);
            // The title bar is taken from the top edge of the rectangle, and the content fits below it
            var bar = Math.Min(window.TitleBar, y1 - y0);
            window.ContentRect = new PixelRect(x0, y0 + bar, x1 - x0, y1 - y0 - bar);
        }
    }
}

// A rectangle on the display (top left and size; display coordinates, +Y down)
public class PixelRect
{
    public readonly float X;
    public readonly float Y;
    public readonly float W;
    public readonly float H;

    public PixelRect(float x, float y, float w, float h)
    {
        X = x;
        Y = y;
        W = w;
        H = h;
    }

    public bool Contains(float x, float y)
    {
        return x >= X && x < X + W && y >= Y && y < Y + H;
    }
}

// A rectangle laid out on a screen. Shows one content (IContent)
public class Window
{
    public IContent Content;
    // Center position in screen coordinates (meters). X is right, Y is up, Z is depth (0 is on the surface, negative is toward the viewer).
    // X and Y are set by the arrangement rule or by grabbing and moving. null for windows not placed yet or not shown
    public Vec3? Position;
    // Width and height (meters). Set together with Position
    public Vec2 Size = new Vec2(0, 0);
    // Location projected onto the display (only for plane screens anchored to the view; set by Screen.Project)
    public PixelRect? ViewportRect;
    // The part of ViewportRect that shows the content (the rectangle without the title bar)
    public PixelRect? ContentRect;
    // Height of the title bar at the top edge of the rectangle (display coordinates; 0 = none). Used for the game picture on plane screens
    public float TitleBar;
    // Pixel size the content is drawn at. The size set by the user (WidthPx, HeightPx) and the current size (PxW, PxH; they differ only during Zen)
    public int WidthPx = PanelView.BasePxW;
    public int HeightPx = PanelView.BasePxH;
    public int PxW = PanelView.BasePxW;
    public int PxH = PanelView.BasePxH;
    // Size scale (at 1, BasePxW pixels are PanelView.BaseW meters)
    public float Scale = 1;
    // Whether it was resized to be readable outside XR
    public bool Sized;

    public const int MinPxW = 320;
    public const int MinPxH = 160;
    public const int MaxPx = 2048;

    // Whether it is stowed (keeps its place in the order, but is not laid out or shown)
    public bool Away;

    // Parent window (if none, the window is in the screen's order).
    // A child is placed in the parent's coordinates as decided by the parent's content (origin at the parent's center, meters, negative Z in front of the parent). Arrangement rules do not lay out children
    public Window? Parent;
    public readonly List<Window> Children = new List<Window>();

    public Window(IContent content)
    {
        Content = content;
    }

    // Makes child a child of this window
    public void Adopt(Window child)
    {
        child.Parent?.Children.Remove(child);
        child.Parent = this;
        Children.Add(child);
    }

    // Removes child from the children
    public void Disown(Window child)
    {
        if (Children.Remove(child))
            child.Parent = null;
    }

    // Width and height at scale 1 (meters; determined by the content's pixel size)
    public Vec2 UnscaledSize => new Vec2(PanelView.BaseW * PxW / PanelView.BasePxW, PanelView.BaseW * PxH / PanelView.BasePxW);

    // Depth (Z of Position; 0 if not set)
    public float Z => Position != null ? Position.Value.Z : 0;

    // Places the center at (x, y) on the screen surface. Depth and size are unchanged
    public void Place(float x, float y)
    {
        Position = new Vec3(x, y, Z);
    }

    // Changes the depth (does nothing if not placed yet)
    public void SetZ(float z)
    {
        if (Position != null)
            Position = new Vec3(Position.Value.X, Position.Value.Y, z);
    }

    // The content, when it is code
    public Code Code => (Code)Content;

    // Changes the user-set size (px). It does not need to align to character columns; the editor decides the columns
    public void Resize(int pxW, int pxH)
    {
        WidthPx = Math.Max(MinPxW, Math.Min(MaxPx, pxW));
        HeightPx = Math.Max(MinPxH, Math.Min(MaxPx, pxH));
    }
}
