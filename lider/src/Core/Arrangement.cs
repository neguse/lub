// Arrangement: which screen is where and with which rule, and which window is on which screen in which order.
// It can be recorded under a name and restored. Both the stacked form and the side-by-side form are arrangements.
// Windows are identified by key: code is "code:" plus the open file (the path from Root joined with "|"), the game picture is "game:saved" and "game:draft", and the inspector is "inspector".
// "code:*" means every code window not on another line, and code windows opened after restoring are also put on the screen that has it.
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public class ScreenSpec
{
    public string Name = "";
    public ScreenShape Shape = ScreenShape.Plane;
    public ScreenAnchor Anchor = ScreenAnchor.View;
    public Vec3 Position = new Vec3(0, 0, 1);
    public Vec2 Size = new Vec2(1, 1);
    public float Opacity = 1;
    // Rule (in the Layouts.Describe notation)
    public string Layout = "Full";
    public List<string> Windows = new List<string>();
}

public class Arrangement
{
    public const string AllCode = "code:*";

    public string Name = "";
    public List<ScreenSpec> Screens = new List<ScreenSpec>();
    // Key of the window placed in Space (empty if none). It is restored to the place in Screens where this key is written
    public string Space = "";

    // Records the current arrangement
    public static Arrangement Capture(string name, Workspace w)
    {
        var a = new Arrangement { Name = name };
        // The window placed in Space is also recorded in the order of the Screen it returns to
        var spaced = w.InSpace;
        w.TakeFromSpace();
        if (spaced != null)
            a.Space = KeyOf(w, spaced);
        var home = w.Home ?? w.CodeScreen;
        foreach (var screen in w.Screens)
        {
            // Screens whose rule stacks from an edge (hints, search) show fixed content, so they are not recorded
            if (screen.Layout is Stack)
                continue;
            var spec = new ScreenSpec
            {
                Name = screen.Name,
                Shape = screen.Shape,
                Anchor = screen.Anchor,
                Position = screen.Position,
                Size = screen.Size,
                Opacity = screen.Opacity,
                Layout = Layouts.Describe(screen.Layout),
            };
            foreach (var window in screen.Windows)
            {
                var key = KeyOf(w, window);
                if (key != "")
                    spec.Windows.Add(key);
            }
            if (screen == home)
                spec.Windows.Add(AllCode);
            a.Screens.Add(spec);
        }
        if (spaced != null)
            w.PutInSpace(spaced);
        return a;
    }

    // Restores the arrangement. Changes the shape and rule of screens with matching names and puts the windows back in the recorded order.
    // Windows not in the arrangement stay on their current screen, after the recorded ones
    public void Apply(Workspace w)
    {
        front.Clear();
        w.TakeFromSpace();
        var all = new List<Window>();
        foreach (var screen in w.Screens)
            if (!(screen.Layout is Stack))
                foreach (var window in screen.Windows)
                    all.Add(window);
        var placed = new List<Window>();
        Screen? home = null;
        // The window listed right after "code:*" (newly opened code windows go before it)
        Window? after = null;
        var afterAll = false;
        // Code windows with no line recording their key by name are placed at "code:*"
        var named = new List<string>();
        foreach (var spec in Screens)
            foreach (var key in spec.Windows)
                named.Add(key);
        foreach (var spec in Screens)
        {
            var screen = ScreenNamed(w, spec.Name);
            if (screen == null)
                continue;
            screen.Shape = spec.Shape;
            screen.Anchor = spec.Anchor;
            screen.Position = spec.Position;
            screen.Size = spec.Size;
            screen.Opacity = spec.Opacity;
            screen.Layout = Layouts.Parse(spec.Layout);
            var order = new List<Window>();
            foreach (var key in spec.Windows)
            {
                if (key == AllCode)
                {
                    home = screen;
                    afterAll = true;
                    foreach (var window in all)
                        if (!placed.Contains(window) && window.Content is Code && !named.Contains(KeyOf(w, window)))
                        {
                            order.Add(window);
                            placed.Add(window);
                        }
                    continue;
                }
                foreach (var window in all)
                    if (!placed.Contains(window) && KeyOf(w, window) == key)
                    {
                        order.Add(window);
                        placed.Add(window);
                        if (afterAll && after == null)
                            after = window;
                        break;
                    }
            }
            afterAll = false;
            Put(w, screen, order);
        }
        w.Home = home == w.CodeScreen ? null : home;
        w.HomeBefore = after;
        if (Space != "")
            foreach (var window in all)
                if (KeyOf(w, window) == Space)
                {
                    w.PutInSpace(window);
                    break;
                }
    }

    // Puts windows into the screen's order in this order (moving any that were on other screens).
    // They follow the ones placed before, and windows not in the arrangement remain after them
    void Put(Workspace w, Screen screen, List<Window> windows)
    {
        var at = front.TryGetValue(screen, out var n) ? n : 0;
        foreach (var window in windows)
        {
            if (w.ScreenOf(window) != screen)
                w.MoveTo(window, screen);
            screen.Windows.Remove(window);
            screen.Windows.Insert(Math.Min(at, screen.Windows.Count), window);
            at++;
        }
        front[screen] = at;
    }

    // During Apply, per screen, how many windows have been placed from the front of the order
    readonly Dictionary<Screen, int> front = new Dictionary<Screen, int>();

    static Screen? ScreenNamed(Workspace w, string name)
    {
        foreach (var screen in w.Screens)
            if (screen.Name == name)
                return screen;
        return null;
    }

    // Key that identifies a window ("" for content the arrangement does not identify)
    public static string KeyOf(Workspace w, Window window)
    {
        if (window.Content is Code code)
        {
            var paths = new List<string>();
            foreach (var doc in code.Tabs)
                paths.Add(w.Relative(doc.Path));
            return "code:" + string.Join("|", paths);
        }
        if (window.Content is GameView view)
            return view.Version == GameVersion.Saved ? "game:saved" : "game:draft";
        if (window.Content is Inspector)
            return "inspector";
        return "";
    }

    // Lines written to the file: "arrangement name", "space key" if a window is placed in Space, "screen name shape anchor x y z width height opacity rule" per screen, and "window key" per window
    public List<string> Lines()
    {
        var lines = new List<string> { "arrangement " + Name };
        if (Space != "")
            lines.Add("space " + Space);
        foreach (var s in Screens)
        {
            lines.Add("screen " + s.Name + " " + (s.Shape == ScreenShape.Plane ? "plane" : "cylinder") + " " + (s.Anchor == ScreenAnchor.View ? "view" : "world")
                + " " + s.Position.X + " " + s.Position.Y + " " + s.Position.Z + " " + s.Size.X + " " + s.Size.Y + " " + s.Opacity + " " + s.Layout);
            foreach (var key in s.Windows)
                lines.Add("window " + key);
        }
        return lines;
    }

    // Reads arrangements from lines in the Lines format (unreadable lines are skipped)
    public static List<Arrangement> Read(string text)
    {
        var all = new List<Arrangement>();
        Arrangement? a = null;
        ScreenSpec? spec = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("arrangement "))
            {
                a = new Arrangement { Name = Utf8.Sub(line, 12) };
                spec = null;
                all.Add(a);
            }
            else if (line.StartsWith("space ") && a != null)
                a.Space = Utf8.Sub(line, 6);
            else if (line.StartsWith("screen ") && a != null)
            {
                var f = line.Split(' ');
                if (f.Length < 11)
                    continue;
                spec = new ScreenSpec
                {
                    Name = f[1],
                    Shape = f[2] == "plane" ? ScreenShape.Plane : ScreenShape.Cylinder,
                    Anchor = f[3] == "view" ? ScreenAnchor.View : ScreenAnchor.World,
                    Position = new Vec3(Number(f[4]), Number(f[5]), Number(f[6])),
                    Size = new Vec2(Number(f[7]), Number(f[8])),
                    Opacity = Number(f[9]),
                };
                var layout = f[10];
                for (var i = 11; i < f.Length; i++)
                    layout += " " + f[i];
                spec.Layout = layout;
                a.Screens.Add(spec);
            }
            else if (line.StartsWith("window ") && spec != null)
                spec.Windows.Add(Utf8.Sub(line, 7));
        }
        return all;
    }

    static float Number(string s)
    {
        return float.TryParse(s, out var v) ? v : 0;
    }
}

// Rules and their notation (a name and values, like "MainStack 0.5")
public static class Layouts
{
    public static string Describe(ILayout layout)
    {
        if (layout is Strip)
            return "Strip";
        if (layout is Even even)
            return even.Rows ? "Even rows" : "Even";
        if (layout is MainStack main)
            return "MainStack mains=" + main.Mains + (main.Widths.Count > 0 ? " widths=" + Numbers(main.Widths) : "") + (main.Heights.Count > 0 ? " heights=" + Numbers(main.Heights) : "");
        if (layout is Stack stack)
            return "Stack " + stack.AlignX + " " + (stack.FromTop ? "top" : "bottom") + " " + stack.Margin;
        return "Full";
    }

    static string Numbers(List<float> values)
    {
        var parts = new List<string>();
        foreach (var v in values)
            parts.Add(v.ToString());
        return string.Join(",", parts);
    }

    static List<float> ParseNumbers(string text)
    {
        var values = new List<float>();
        foreach (var part in text.Split(','))
            if (float.TryParse(part, out var v))
                values.Add(v);
        return values;
    }

    public static ILayout Parse(string text)
    {
        var f = text.Split(' ');
        if (f[0] == "Strip")
            return new Strip();
        if (f[0] == "Even")
            return new Even { Rows = f.Length > 1 && f[1] == "rows" };
        if (f[0] == "MainStack")
        {
            var main = new MainStack();
            for (var i = 1; i < f.Length; i++)
            {
                var pair = f[i].Split('=');
                // A number without a name is the width ratio for the first window (the old notation)
                if (pair.Length == 1 && float.TryParse(f[i], out var ratio) && ratio > 0 && ratio < 1)
                    main.Widths = new List<float> { ratio, 1 - ratio };
                else if (pair.Length == 2 && pair[0] == "mains" && int.TryParse(pair[1], out var mains))
                    main.Mains = mains;
                else if (pair.Length == 2 && pair[0] == "widths")
                    main.Widths = ParseNumbers(pair[1]);
                else if (pair.Length == 2 && pair[0] == "heights")
                    main.Heights = ParseNumbers(pair[1]);
            }
            return main;
        }
        if (f[0] == "Stack" && f.Length > 3)
            return new Stack { AlignX = float.TryParse(f[1], out var x) ? x : 1, FromTop = f[2] == "top", Margin = float.TryParse(f[3], out var m) ? m : 0.03f };
        return new Full();
    }
}
