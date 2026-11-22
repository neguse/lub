// Alt+L (arrange:next) changes the arrangement: it toggles between the layout where code windows overlay the game view and the side-by-side layout.
//   - In the side-by-side layout, the code windows move to the background screen (a plane fixed to the viewpoint) and appear on the right half of the display
//   - In that window, pressing the middle of the right half twice in a row selects a word (where it is drawn matches where it is hit)
//   - Pressing the left half of the display does not hit the code window
//   - Grabbing a window with the right button and dragging it into the game view's tile swaps the order
//   - Alt+L again goes back to the code screen
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestTiled
{
    const string Word = "abcdefghijklmnopqrstuvwxyz";
    static int stage;
    static float next;
    static bool failed;
    static int id;
    // Remaining time of the window's opening animation. The animation advances by dt, so with heavy frames it does not finish even when waiting in real time
    static float opening;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
        Lider.OnInit();
        // Start from the overlay layout and switch to the side-by-side layout
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

    static void TestFrame(float dt)
    {
        opening -= dt;
        if (failed || opening > 0 || Sys.Seconds() < next)
            return;
        next = Sys.Seconds() + 0.5f;
        Gfx.Size(out var sw, out var sh);
        var w = Lider.Work!;
        if (stage == 0)
        {
            var path = Environment.GetEnvironmentVariable("LIDER_TEST_FILE");
            Check(path != null, "LIDER_TEST_FILE is missing");
            if (failed)
                return;
            Lider.Inspector.Shown = false;
            w.FocusOn(path!);
            id = w.FocusWindow!.Code.ViewId;
            opening = 1.5f;
        }
        else if (stage == 1)
        {
            Check(w.ScreenOf(w.FocusWindow!) == w.CodeScreen, "the window did not start on the code screen");
            NextLayout();
            Check(Lider.Tiled && w.ScreenOf(w.FocusWindow!) == Lider.Screens[0], "Alt+L did not move the window to the backdrop");
            opening = 1.5f;
        }
        else if (stage == 2)
        {
            var at = w.FocusWindow!.ViewportRect;
            Check(at != null && Math.Abs(at.X - sw * 0.5f) <= 1 && Math.Abs(at.W - sw * 0.5f) <= 1 && at.Y == 0 && Math.Abs(at.H - sh) <= 1,
                "the window is not the right half: " + (at != null ? at.X + "," + at.Y + " " + at.W + "x" + at.H : "none"));
            var game = Lider.Screens[0].Windows[0].ViewportRect;
            Check(game != null && game.X == 0 && Math.Abs(game.W - sw * 0.5f) <= 1, "the game is not the left half");
            // Pressing the left half does not hit the code window
            EditorHost.Focus(id);
            w.FocusWindow.Code.Doc.MoveTo(0, 0);
            var before = EditorHost.CursorLine(id);
            Click(sw * 0.25f, sh * 0.5f);
            Check(EditorHost.CursorLine(id) == before, "a click on the game moved the cursor to line " + EditorHost.CursorLine(id));
            // Pressing the middle of the right half twice selects the word there
            Click(sw * 0.75f, sh * 0.5f);
            Click(sw * 0.75f, sh * 0.5f);
            Check(EditorHost.Selection(id) == Word, "a double click on the tiled window selected '" + EditorHost.Selection(id) + "'");
        }
        else if (stage == 3)
        {
            // Grabbing the window with the right button and dragging it to the left half (the game view's tile) swaps the order
            Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = 3, X = sw * 0.75f, Y = sh * 0.5f });
            Lider.OnEvent(new EventData { Kind = EventKind.MouseMotion, X = sw * 0.25f, Y = sh * 0.5f });
        }
        else if (stage == 4)
        {
            Check(Lider.Screens[0].Windows[0] == w.FocusWindow, "dragging the window over the game did not swap the order");
            Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonUp, Button = 3, X = sw * 0.25f, Y = sh * 0.5f });
        }
        else if (stage == 5)
        {
            var at = w.FocusWindow!.ViewportRect;
            Check(at != null && at.X == 0 && Math.Abs(at.W - sw * 0.5f) <= 1, "the dropped window is not the left half");
            var game = Lider.Screens[0].Windows[1].ViewportRect;
            Check(Lider.Screens[0].Windows[1].Content is GameView && game != null && Math.Abs(game.X - sw * 0.5f) <= 1, "the game is not the right half");
            // Put it back
            Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = 3, X = sw * 0.25f, Y = sh * 0.5f });
            Lider.OnEvent(new EventData { Kind = EventKind.MouseMotion, X = sw * 0.75f, Y = sh * 0.5f });
        }
        else if (stage == 6)
        {
            Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonUp, Button = 3, X = sw * 0.75f, Y = sh * 0.5f });
            Check(Lider.Screens[0].Windows[0].Content is GameView, "dragging the window back did not restore the order");
        }
        else if (stage == 7)
        {
            NextLayout();
            Check(!Lider.Tiled && w.ScreenOf(w.FocusWindow!) == w.CodeScreen, "Alt+L did not move the window back");
            Check(Lider.Screens[0].Windows.Count == 1, "the backdrop kept " + Lider.Screens[0].Windows.Count + " windows");
            opening = 1.5f;
        }
        else if (stage == 8)
        {
            // Back in the overlay layout, the window is in the middle of the display
            Click(sw * 0.5f, sh * 0.5f);
            Click(sw * 0.5f, sh * 0.5f);
            Check(EditorHost.Selection(id) == Word, "a double click after going back selected '" + EditorHost.Selection(id) + "'");
        }
        else
        {
            Console.WriteLine("PASS");
            App.Quit(0);
            return;
        }
        if (!failed && EditorHost.LastError() != "")
            Check(false, "editor error: " + EditorHost.LastError());
        stage++;
    }

    // Alt+L (to the next arrangement)
    static void NextLayout()
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = 15, Mods = Keys.ModAlt });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = 15, Mods = 0 });
    }

    static void Click(float x, float y)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = 1, X = x, Y = y });
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonUp, Button = 1, X = x, Y = y });
    }

    static void Check(bool cond, string message)
    {
        if (cond || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL: " + message);
        App.Quit(1);
    }
}
