// lub discards textures whose declarations have stopped, so a board whose declaration stopped is redrawn entirely.
//   - Closing a window, waiting SweepFrames, and reopening it redraws everything
//   - Hiding lider, waiting SweepFrames, and showing it again redraws everything
//   - If the declaration has not stopped, typing redraws only the band (the characters of the hint that typing shows are put into the atlas first, then measured)
using System;
using Lub;
using LiderCore;

public static class TestReopen
{
    // Wait longer than the number of frames until lub discards it
    const int SweepFrames = 130;
    static int frames;
    static int stage;
    static int waitUntil;
    static bool failed;
    static string file = "";
    static Window? closed;

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
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    static void TestFrame(float dt)
    {
        frames++;
        if (failed || frames < waitUntil)
            return;
        var w = Lider.Work!;
        switch (stage)
        {
            case 0:
                file = Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!;
                w.FocusOn(file);
                waitUntil = frames + 10;
                break;
            case 1:
                // Typing shows a hint (F9). At density 1, the hint and the window share the glyph atlas, and a character appearing in the hint for the first time redraws the whole window, so it is shown first
                w.Focus!.Type("x");
                waitUntil = frames + 3;
                break;
            case 2:
                Lider.Panel(w.FocusWindow!).RedrawnLines = Untouched;
                w.Focus!.Type("x");
                waitUntil = frames + 3;
                break;
            case 3:
                Check(Lider.Panel(w.FocusWindow!).RedrawnLines >= 0, "typing redrew everything: " + Lider.Panel(w.FocusWindow!).RedrawnLines);
                closed = w.FocusWindow;
                w.CloseTab();
                break;
            case 4:
                // Declarations continue during the closing animation
                if (Lider.MotionOf(closed!) != null)
                    return;
                Lider.Panel(closed!).RedrawnLines = Untouched;
                waitUntil = frames + SweepFrames;
                break;
            case 5:
                w.FocusOn(file);
                waitUntil = frames + 3;
                break;
            case 6:
                Check(Lider.Panel(w.FocusWindow!).RedrawnLines == -1, "reopening did not redraw everything: " + Lider.Panel(w.FocusWindow!).RedrawnLines);
                Lider.Panel(w.FocusWindow!).RedrawnLines = Untouched;
                Lider.SetHidden(true);
                waitUntil = frames + SweepFrames;
                break;
            case 7:
                Lider.FocusCode();
                waitUntil = frames + 3;
                break;
            default:
                Check(Lider.Panel(w.FocusWindow!).RedrawnLines == -1, "showing lider again did not redraw everything: " + Lider.Panel(w.FocusWindow!).RedrawnLines);
                if (failed)
                    return;
                Console.WriteLine("PASS");
                App.Quit(0);
                break;
        }
        stage++;
    }

    // Marker for "not redrawn"
    const int Untouched = -2;

    static void Check(bool ok, string message)
    {
        if (ok || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}
