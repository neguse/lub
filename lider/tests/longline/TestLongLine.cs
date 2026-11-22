// Lines longer than the window, and horizontal scrolling of the window.
//   - Moving the cursor to the end of a line longer than the window scrolls the window horizontally, and the cursor is visible inside the window
//   - Moving to a line where the cursor is visible without scrolling returns the window to the left edge
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestLongLine
{
    // The line of Target.cs that is longer than the window, and the short line below it (0-based)
    const int LongLine = 3;
    const int ShortLine = 4;
    static int frames;
    static bool failed;
    static float waited;
    static int stage;
    static float left;

    public static void OnInit()
    {
        TestInit();
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
        TestFrame(dt);
        Lider.OnFrame(dt);
    }

    static void TestInit()
    {
        App.Config(new ConfigOpts { Width = 1280, Height = 720 });
    }

    static void TestFrame(float dt)
    {
        frames++;
        waited += dt;
        if (failed)
            return;
        var w = Lider.Work!;
        if (frames == 2)
        {
            waited = 0;
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
            w.Focus!.MoveTo(LongLine, 0);
            return;
        }
        // Wait until the window has opened and stopped (2 seconds)
        if (frames < 3 || waited < 1.5f)
            return;
        var doc = w.Focus!;
        var view = w.FocusWindow!.Code.ViewId;
        if (stage == 0)
        {
            Check(InWindow(w), "the caret at the start of the long line is outside the window");
            left = EditorHost.TextX(view);
            doc.MoveTo(LongLine, Utf8.Bytes(doc.Text(LongLine)));
        }
        else if (stage == 1)
        {
            Check(EditorHost.TextX(view) < left, "the window did not scroll to the end of the long line: text at " + EditorHost.TextX(view) + ", left " + left);
            Check(InWindow(w), "the caret at the end of the long line is outside the window");
            doc.MoveTo(ShortLine, 4);
        }
        else
        {
            Check(EditorHost.TextX(view) == left, "the window stayed scrolled on a short line: text at " + EditorHost.TextX(view) + ", want " + left);
            Check(InWindow(w), "the caret on the short line is outside the window");
            if (failed)
                return;
            Console.WriteLine("PASS");
            App.Quit(0);
            return;
        }
        stage++;
        waited = 1;
    }

    // Whether the cursor of the focused window (Lider.CaretViewport) is between the left and right edges of the window
    static bool InWindow(Workspace w)
    {
        var window = w.FocusWindow!;
        var angle = Lider.WindowAngle(w, window);
        var half = window.UnscaledSize.X * window.Scale * 0.5f;
        var y = Lider.WindowY(w, window);
        var l = Lider.ViewportPoint(Space.At(angle - half / Space.R, y, Space.R));
        var r = Lider.ViewportPoint(Space.At(angle + half / Space.R, y, Space.R));
        var x = Lider.CaretViewport.X;
        if (x > l.X && x < r.X)
            return true;
        Console.WriteLine("caret x " + x + ", window " + l.X + " - " + r.X);
        return false;
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
