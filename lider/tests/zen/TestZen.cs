// Window resizing and Zen.
//   - Alt+Shift+PageUp and dragging the right edge of a window change the width of only that window. The text density does not change, so more of long lines is visible
//   - Width and height have lower limits
//   - F11 makes the focused window fill the display, and pressing it again returns it to the user's size. Neighboring windows do not change
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestZen
{
    static int frames;
    static bool failed;

    public static void OnInit()
    {
        TestInit();
        Lider.OnInit();
        // Check in the overlay layout (code windows lined up in a horizontal row on the cylindrical screen in space)
        Lider.Arrange(Lider.Overlay);
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
        TestFrame(dt);
        Lider.OnFrame(dt);
    }

    static void TestInit()
    {
        App.Config(new ConfigOpts { Width = 1280, Height = 720 });
    }

    static void TestFrame(float dt)
    {
        if (failed)
            return;
        frames++;
        var w = Lider.Work!;
        var path = Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!;
        if (frames == 2)
            w.FocusOn(path);
        if (frames == 4)
        {
            // Create the neighboring window (opening puts it in the current window's tabs, so move it to the window on the right)
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_ROOT") + "/TestZen.cs");
            w.MoveTab(1);
        }
        // After lider places the 2 windows, advance 1 second until the windows finish expanding (do not rely on the progress made by the heavy frame that opened them)
        if (frames == 6)
            for (var i = 0; i < 60; i++)
                Lider.Animate(w, 1.0f / 60);
        if (frames < 8)
            return;
        var other = w.Windows[1];
        var window = w.Windows[0];
        if (frames == 8)
        {
            w.FocusWindow = window;
            Check(window.PxW == PanelView.BasePxW, "the width at first: " + window.PxW);
            Alt(Keys.PageUp);
            Check(window.WidthPx == PanelView.BasePxW + 64, "Alt+Shift+PageUp: " + window.WidthPx);
            Check(other.WidthPx == PanelView.BasePxW, "the neighbor changed: " + other.WidthPx);
        }
        if (frames == 10)
        {
            Check(window.PxW == PanelView.BasePxW + 64 && Lider.Panel(window).PxW == window.PxW, "the panel width: " + Lider.Panel(window).PxW);
            Check(Longest(window.Code.ViewId, window.PxW) > PanelView.BasePxW, "the long line is cut at the old width");
            // Drag the right edge
            var scale = window.Scale;
            var angle = Lider.WindowAngle(w, window);
            var right = angle + (window.UnscaledSize.X * scale * 0.5f + 0.005f) / Space.R;
            var ray = new Ray(new Vec3(0, 0, 0), Space.At(right, Lider.WindowY(w, window), Space.R));
            var edge = Lider.EdgeAt(w, window, ray);
            Check((edge & 1) != 0, "the right edge is not found: " + edge);
            var before = window.WidthPx;
            Lider.StartEdge(window, 1);
            Lider.DragEdge(w, new Ray(new Vec3(0, 0, 0), Space.At(right + 0.1f / Space.R, Lider.WindowY(w, window), Space.R)));
            Lider.EndEdge();
            Check(window.WidthPx > before, "dragging did not widen: " + before + " -> " + window.WidthPx);
            window.Resize(1, 1);
            Check(window.WidthPx == Window.MinPxW && window.HeightPx == Window.MinPxH, "no lower limit: " + window.WidthPx);
            window.Resize(900, PanelView.BasePxH);
        }
        if (frames == 12)
            Press();
        if (frames == 16)
        {
            Check(window.PxW > 900, "F11 did not widen the window: " + window.PxW);
            Check(window.WidthPx == 900, "F11 changed the chosen width: " + window.WidthPx);
            Check(other.PxW == PanelView.BasePxW, "F11 widened the neighbor: " + other.PxW);
            Press();
        }
        if (frames == 20)
        {
            Check(window.PxW == 900, "F11 did not restore: " + window.PxW);
            if (!failed)
            {
                Console.WriteLine("PASS");
                App.Quit(0);
            }
        }
    }

    static void Alt(int key)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = Keys.ModAlt | Keys.ModShift, Typed = 0, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = 0, Typed = 0, Plain = 0 });
    }

    // Make it visible down to the last line, and measure the right edge of the draw commands
    static float Longest(int id, int pxW)
    {
        EditorHost.ScrollToLine(id, 90);
        var n = EditorHost.Draw(id, pxW, PanelView.BasePxH);
        var right = 0f;
        for (var i = 0; i < n; i++)
            if (EditorHost.CmdKind(i) == 2)
                right = Math.Max(right, EditorHost.CmdX(i) + EditorHost.CmdW(i));
        return right;
    }

    static void Press()
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = Keys.Return, Mods = Keys.ModAlt, Typed = 0, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = Keys.Return, Mods = 0, Typed = 0, Plain = 0 });
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
