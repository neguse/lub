// User operations change the settings of the tiling rule (MainStack).
//   - In the side-by-side layout at startup, the first window (the game view) is in the left column and the rest are stacked in the right column
//   - Using a command to increase the number of windows lined up one per column at the front adds columns (game view | Code | Inspector). Decreasing it goes back
//   - Dragging a column border (the right edge of a window) with the mouse changes the column width ratio
//   - Dragging the border between stacked windows (the bottom edge) changes the height ratio
//   - A command can change the width of the focused window (for the rightmost column, the left border moves)
//   - Changed settings appear in the written form of the rule, and reloading gives the same layout
//   - Zooming (the wheel outside windows) does not move the flat screen
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestTiles
{
    static int frames;
    static int stage;
    static bool failed;
    static PixelRect? kept;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 1200, Height = 600 });
        Lider.OnInit();
    }

    public static void OnEvent(EventData e)
    {
        Lider.OnEvent(e);
    }

    public static void OnQuit()
    {
        Lider.OnQuit();
    }

    public static void OnFrame(float dt)
    {
        TestFrame();
        Lider.OnFrame(dt);
    }

    static void TestFrame()
    {
        if (failed)
            return;
        frames++;
        var w = Lider.Work!;
        var backdrop = Lider.Screens[0];
        if (frames == 2)
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
        // After each operation, wait until the layout is redone and the rectangles on the display are settled
        if (frames < 10 || frames % 5 != 0)
            return;
        var game = backdrop.Windows[0];
        var code = w.FocusWindow!;
        var inspector = Lider.InspectWindow;
        var g = game.ViewportRect;
        var c = code.ViewportRect;
        var i = inspector.ViewportRect;
        Check(g != null && c != null && i != null, "a window on the backdrop has no rectangle at stage " + stage);
        if (failed)
            return;
        switch (stage++)
        {
            case 0:
                Check(Near(g!.W, 600) && Near(c!.X, 600) && Near(c.W, 600) && Near(c.H, 300) && Near(i!.X, 600) && Near(i.Y, 300),
                    "at first: game " + Show(g) + " code " + Show(c!) + " inspector " + Show(i!));
                Lider.Run("layout:more-mains");
                break;
            case 1:
                Check(Near(g!.W, 400) && Near(c!.X, 400) && Near(c.W, 400) && Near(c.H, 600) && Near(i!.X, 800) && Near(i.H, 600),
                    "with two mains: game " + Show(g) + " code " + Show(c!) + " inspector " + Show(i!));
                Lider.Run("layout:fewer-mains");
                break;
            case 2:
                Check(Near(g!.W, 600) && Near(c!.H, 300), "back to one main: game " + Show(g) + " code " + Show(c!));
                // Press just outside the right edge of the game view and drag left
                Mouse(EventKind.MouseButtonDown, g.X + g.W + 3, 150);
                Mouse(EventKind.MouseMotion, 450, 150);
                Mouse(EventKind.MouseButtonUp, 450, 150);
                Check(w.Focused == code, "dragging an edge moved the focus");
                break;
            case 3:
                Check(Near(g!.W, 450) && Near(c!.X, 450) && Near(c.W, 750) && Near(i!.W, 750),
                    "after dragging the column edge: game " + Show(g) + " code " + Show(c!) + " inspector " + Show(i!));
                // Press just outside the bottom edge of Code and drag down
                Mouse(EventKind.MouseButtonDown, 800, c.Y + c.H + 3);
                Mouse(EventKind.MouseMotion, 800, 420);
                Mouse(EventKind.MouseButtonUp, 800, 420);
                break;
            case 4:
                Check(Near(c!.H, 420) && Near(i!.Y, 420) && Near(i.H, 180),
                    "after dragging the stacked edge: code " + Show(c) + " inspector " + Show(i!));
                var text = Layouts.Describe(backdrop.Layout);
                Check(text.Contains("mains=1") && text.Contains("widths=") && text.Contains("heights="), "the layout is written as " + text);
                var before = g!.W;
                backdrop.Layout = Layouts.Parse(text);
                backdrop.Arrange();
                Check(Near(game.Size.X / backdrop.Size.X * 1200, before), "the layout read back differs: " + Layouts.Describe(backdrop.Layout));
                // Focus is on Code (the rightmost column). Widening it moves the left border
                Lider.Run("window:widen");
                break;
            case 5:
                Check(g!.W < 445 && Near(c!.X, g.W) && Near(c.X + c.W, 1200), "after window:widen: game " + Show(g) + " code " + Show(c!));
                // Zooming (the wheel outside windows) does not move the flat screen
                kept = g;
                // The test cannot set the mouse position (the wheel is received at the current position), so it is sent while dragging an edge (while lider receives the mouse input)
                Lider.StartEdge(game, 1);
                Lider.OnEvent(new EventData { Kind = EventKind.MouseWheel, Dy = 3 });
                Lider.EndEdge();
                break;
            case 6:
                Check(Near(g!.X, kept!.X) && Near(g.W, kept.W) && Near(g.H, kept.H), "zooming moved the plane screen: game " + Show(kept) + " -> " + Show(g));
                Console.WriteLine("PASS");
                App.Quit(0);
                break;
        }
    }

    static bool Near(float a, float b)
    {
        return Math.Abs(a - b) <= 2;
    }

    static string Show(PixelRect r)
    {
        return r.X + "," + r.Y + " " + r.W + "x" + r.H;
    }

    static void Mouse(EventKind kind, float x, float y)
    {
        Lider.OnEvent(new EventData { Kind = kind, Button = 1, X = x, Y = y });
    }

    static void Check(bool ok, string message)
    {
        if (ok || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}
