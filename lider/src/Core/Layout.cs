// Arrangement rules: lay out windows on a screen.
// From the window order and the screen size, determines each window's position on the surface (X and Y of Position) and size (Size).
// Depth (Z of Position) is not changed.
using System;
using Lub;

namespace LiderCore;

public interface ILayout
{
    void Arrange(Screen screen);
    // Edges of window that can be dragged to resize (sum of 1 for right and 2 for bottom)
    int Edges(Screen screen, Window window);
    // Grows window's width by dx and height by dy meters (shrinks it if negative). The rule decides which boundary moves
    void Grow(Screen screen, Window window, float dx, float dy);
    // A message to the rule (changes per-rule settings). Unknown messages do nothing and return false
    bool Tell(string message);
}

// Message names
public static class LayoutMessage
{
    // Increase or decrease the number of leading windows laid out one per column
    public const string MoreMains = "mains+";
    public const string FewerMains = "mains-";
}

// A single horizontal row. Windows keep their own size (Window.UnscaledSize and Scale) and are laid out from the left with Gap between them.
// The center of the Screen.Origin window (the first window if none) is placed at the center of the screen.
// The held window (Screen.Held) is not moved, and when it crosses the center of a neighboring window they swap places in the order
public class Strip : ILayout
{
    // Gap between windows (meters)
    public const float Gap = 0.06f;

    public void Arrange(Screen screen)
    {
        var windows = screen.Windows;
        var held = screen.Held;
        if (held != null && held.Position != null && windows.Contains(held))
        {
            var at = windows.IndexOf(held);
            var x = held.Position.Value.X;
            if (at + 1 < windows.Count && windows[at + 1].Position != null && x > windows[at + 1].Position!.Value.X)
                Swap(windows, at, at + 1);
            else if (at > 0 && windows[at - 1].Position != null && x < windows[at - 1].Position!.Value.X)
                Swap(windows, at, at - 1);
        }
        // Center of Origin when laid out with the first window's center at 0
        var origin = 0.0f;
        var right = 0.0f;
        for (var i = 0; i < windows.Count; i++)
        {
            var half = windows[i].UnscaledSize.X * windows[i].Scale * 0.5f;
            var center = i == 0 ? 0 : right + half;
            right = center + half + Gap;
            if (windows[i] == screen.Origin)
                origin = center;
        }
        right = 0.0f;
        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            var size = window.UnscaledSize * window.Scale;
            var half = size.X * 0.5f;
            var center = i == 0 ? 0 : right + half;
            right = center + half + Gap;
            if (window == held)
                continue;
            window.Place(center - origin, 0);
            window.Size = size;
        }
    }

    // Code windows are the ones with their own size
    public int Edges(Screen screen, Window window)
    {
        return window.Content is Code ? 3 : 0;
    }

    public void Grow(Screen screen, Window window, float dx, float dy)
    {
        if (!(window.Content is Code))
            return;
        var perPx = PanelView.BaseW / PanelView.BasePxW * window.Scale;
        window.Resize(window.WidthPx + (int)Math.Round(dx / perPx), window.HeightPx + (int)Math.Round(dy / perPx));
    }

    public bool Tell(string message)
    {
        return false;
    }

    static void Swap(System.Collections.Generic.List<Window> windows, int a, int b)
    {
        var w = windows[a];
        windows[a] = windows[b];
        windows[b] = w;
    }
}

// Shared by rules that split the screen into cells: assigns cell centers and sizes to windows in order.
// The held window (Screen.Held) is not moved, and when its center enters another cell it swaps places in the order with that cell's window
public static class Tiles
{
    public static void Assign(Screen screen, System.Collections.Generic.List<Vec2> centers, System.Collections.Generic.List<Vec2> sizes)
    {
        var windows = screen.Windows;
        var held = screen.Held;
        var at = held != null ? windows.IndexOf(held) : -1;
        if (held != null && held.Position != null && at >= 0)
        {
            var p = held.Position.Value;
            for (var i = 0; i < windows.Count; i++)
            {
                if (i == at || System.Math.Abs(p.X - centers[i].X) > sizes[i].X * 0.5f || System.Math.Abs(p.Y - centers[i].Y) > sizes[i].Y * 0.5f)
                    continue;
                windows[at] = windows[i];
                windows[i] = held;
                break;
            }
        }
        for (var i = 0; i < windows.Count; i++)
        {
            windows[i].Size = sizes[i];
            if (windows[i] != held || windows[i].Position == null)
                windows[i].Place(centers[i].X, centers[i].Y);
        }
    }
}

// Shows windows in order, split evenly into columns (rows with Rows)
public class Even : ILayout
{
    public bool Rows;

    public void Arrange(Screen screen)
    {
        var n = screen.Windows.Count;
        var w = screen.Size.X;
        var h = screen.Size.Y;
        var centers = new System.Collections.Generic.List<Vec2>();
        var sizes = new System.Collections.Generic.List<Vec2>();
        for (var i = 0; i < n; i++)
        {
            centers.Add(Rows ? new Vec2(0, h * 0.5f - h * (i + 0.5f) / n) : new Vec2(-w * 0.5f + w * (i + 0.5f) / n, 0));
            sizes.Add(Rows ? new Vec2(w, h / n) : new Vec2(w / n, h));
        }
        Tiles.Assign(screen, centers, sizes);
    }

    public int Edges(Screen screen, Window window)
    {
        return 0;
    }

    public void Grow(Screen screen, Window window, float dx, float dy)
    {
    }

    public bool Tell(string message)
    {
        return false;
    }
}

// Lays out the first Mains windows from the left, one per column, and stacks the rest from the top in the rightmost column.
// Column widths and stacked window heights are divided by weight ratios (Widths, Heights). Where no weight is given, the average of the given weights is used.
// If there are no remaining windows, the leading windows alone divide the screen
public class MainStack : ILayout
{
    public int Mains = 1;
    // Column width weights (from the left) and stacked window height weights (from the top)
    public System.Collections.Generic.List<float> Widths = new System.Collections.Generic.List<float>();
    public System.Collections.Generic.List<float> Heights = new System.Collections.Generic.List<float>();
    // Minimum ratio left to one column or one window
    const float MinShare = 0.1f;

    // Number of windows laid out one per column, and the number of columns (the stack column counts as one)
    static int MainsOf(int mains, int n)
    {
        return System.Math.Max(0, System.Math.Min(mains, n));
    }

    static int ColumnsOf(int mains, int n)
    {
        return MainsOf(mains, n) + (n > MainsOf(mains, n) ? 1 : 0);
    }

    // Ratios of the first n weights in weights. Where no weight is given, the average of the given weights is used (an equal split if none is given)
    static System.Collections.Generic.List<float> Shares(System.Collections.Generic.List<float> weights, int n)
    {
        var known = 0.0f;
        var count = 0;
        for (var i = 0; i < n && i < weights.Count; i++)
            if (weights[i] > 0)
            {
                known += weights[i];
                count++;
            }
        var rest = count > 0 ? known / count : 1;
        var shares = new System.Collections.Generic.List<float>();
        var sum = 0.0f;
        for (var i = 0; i < n; i++)
        {
            shares.Add(i < weights.Count && weights[i] > 0 ? weights[i] : rest);
            sum += shares[i];
        }
        for (var i = 0; i < n; i++)
            shares[i] /= sum;
        return shares;
    }

    public void Arrange(Screen screen)
    {
        var n = screen.Windows.Count;
        var w = screen.Size.X;
        var h = screen.Size.Y;
        if (n == 0)
            return;
        var mains = MainsOf(Mains, n);
        var widths = Shares(Widths, ColumnsOf(Mains, n));
        var heights = Shares(Heights, n - mains);
        var centers = new System.Collections.Generic.List<Vec2>();
        var sizes = new System.Collections.Generic.List<Vec2>();
        var left = -w * 0.5f;
        for (var i = 0; i < mains; i++)
        {
            centers.Add(new Vec2(left + w * widths[i] * 0.5f, 0));
            sizes.Add(new Vec2(w * widths[i], h));
            left += w * widths[i];
        }
        var top = h * 0.5f;
        for (var i = mains; i < n; i++)
        {
            var share = heights[i - mains];
            centers.Add(new Vec2(left + w * widths[mains] * 0.5f, top - h * share * 0.5f));
            sizes.Add(new Vec2(w * widths[mains], h * share));
            top -= h * share;
        }
        Tiles.Assign(screen, centers, sizes);
    }

    // The right edge can be dragged on windows with a column to their right. The bottom edge can be dragged on windows with a window stacked below them
    public int Edges(Screen screen, Window window)
    {
        var n = screen.Windows.Count;
        var at = screen.Windows.IndexOf(window);
        if (at < 0 || window.Position == null)
            return 0;
        var mains = MainsOf(Mains, n);
        var column = System.Math.Min(at, mains);
        return (column + 1 < ColumnsOf(Mains, n) ? 1 : 0) + (at >= mains && at + 1 < n ? 2 : 0);
    }

    // Horizontally, moves the right boundary of window's column (for the rightmost column, the left boundary). Vertically, moves the bottom boundary of the stacked window (for the bottom one, the top boundary)
    public void Grow(Screen screen, Window window, float dx, float dy)
    {
        var n = screen.Windows.Count;
        var at = screen.Windows.IndexOf(window);
        if (at < 0 || screen.Size.X <= 0 || screen.Size.Y <= 0)
            return;
        var mains = MainsOf(Mains, n);
        var columns = ColumnsOf(Mains, n);
        var column = System.Math.Min(at, mains);
        if (dx != 0 && columns > 1)
            Widths = Moved(Shares(Widths, columns), column, dx / screen.Size.X);
        if (dy != 0 && at >= mains && n - mains > 1)
            Heights = Moved(Shares(Heights, n - mains), at - mains, dy / screen.Size.Y);
    }

    // Ratios after growing the at-th entry by by (a ratio of the whole) and taking that amount from its neighbor (the next one, or the previous one if it is the last)
    static System.Collections.Generic.List<float> Moved(System.Collections.Generic.List<float> shares, int at, float by)
    {
        var other = at + 1 < shares.Count ? at + 1 : at - 1;
        var both = shares[at] + shares[other];
        var grown = System.Math.Max(MinShare, System.Math.Min(both - MinShare, shares[at] + by));
        shares[at] = grown;
        shares[other] = both - grown;
        return shares;
    }

    public bool Tell(string message)
    {
        if (message == LayoutMessage.MoreMains)
            Mains++;
        else if (message == LayoutMessage.FewerMains)
            Mains = System.Math.Max(0, Mains - 1);
        else
            return false;
        return true;
    }
}

// Stacks windows vertically from a screen edge. Sizes stay as the windows have them.
// Horizontally aligned by AlignX (-1 left edge, 0 center, 1 right edge); vertically stacked downward from the top edge if FromTop, otherwise upward from the bottom edge
public class Stack : ILayout
{
    public float AlignX = 1;
    public bool FromTop;
    // Gap between the edge and windows, and between windows
    public float Margin = 0.03f;

    public void Arrange(Screen screen)
    {
        var edge = screen.Size.Y * 0.5f - Margin;
        foreach (var window in screen.Windows)
        {
            var x = AlignX * (screen.Size.X * 0.5f - Margin - window.Size.X * 0.5f);
            window.Place(x, FromTop ? edge - window.Size.Y * 0.5f : -edge + window.Size.Y * 0.5f);
            edge -= window.Size.Y + Margin;
        }
    }
    public int Edges(Screen screen, Window window)
    {
        return 0;
    }

    public void Grow(Screen screen, Window window, float dx, float dy)
    {
    }

    public bool Tell(string message)
    {
        return false;
    }
}

// Shows only the focused window (the first if none), filling the screen. Other windows are not shown
public class Full : ILayout
{
    public void Arrange(Screen screen)
    {
        var shown = screen.Focus ?? (screen.Windows.Count > 0 ? screen.Windows[0] : null);
        foreach (var window in screen.Windows)
        {
            if (window != shown)
            {
                window.Position = null;
                continue;
            }
            window.Place(0, 0);
            window.Size = screen.Size;
        }
    }
    public int Edges(Screen screen, Window window)
    {
        return 0;
    }

    public void Grow(Screen screen, Window window, float dx, float dy)
    {
    }

    public bool Tell(string message)
    {
        return false;
    }
}
