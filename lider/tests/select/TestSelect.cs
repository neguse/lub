// Selects with the mouse inside lider's window. Events are fed to Lider.OnEvent, the same as those that come from lub.
// The window is placed in front (angle 0), so the center of the display hits a character on the middle line of the window. Every line of Target.cs is the same.
//   - Pressing, moving down, and releasing selects several lines from the pressed point. Moving after releasing does not change the selection
//   - Pressing twice in a row selects a word, and three times selects a line
using System;
using Lub;
using LiderEditor;

public static class TestSelect
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
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
    }

    static void TestFrame(float dt)
    {
        opening -= dt;
        if (failed || opening > 0 || Sys.Seconds() < next)
            return;
        // Wait longer than 0.4 seconds so it does not count as a press in a row with the press of the previous stage
        next = Sys.Seconds() + 0.5f;
        Gfx.Size(out var sw, out var sh);
        float cx = sw / 2, cy = sh / 2;
        if (stage == 0)
        {
            var w = Lider.Work;
            var path = Environment.GetEnvironmentVariable("LIDER_TEST_FILE");
            Check(w != null && path != null, "LIDER_TEST_FILE or the workspace is missing");
            if (failed)
                return;
            // Put the inspector away so the window is placed in the middle of the screen
            Lider.Inspector.Shown = false;
            w!.FocusOn(path!);
            id = w.FocusWindow!.Code.ViewId;
            // While it is moving, point at it using its visible size
            opening = 1.5f;
        }
        else if (stage == 1)
        {
            Mouse(EventKind.MouseButtonDown, cx, cy);
            Mouse(EventKind.MouseMotion, cx, cy + 60);
            Mouse(EventKind.MouseButtonUp, cx, cy + 60);
            var s = EditorHost.Selection(id);
            Check(s.Split('\n').Length >= 3, "dragging down selected '" + s + "'");
            Mouse(EventKind.MouseMotion, cx, cy + 120);
            Check(EditorHost.Selection(id) == s, "moving after the release changed the selection to '" + EditorHost.Selection(id) + "'");
        }
        else if (stage == 2)
        {
            Mouse(EventKind.MouseButtonDown, cx, cy);
            Mouse(EventKind.MouseButtonUp, cx, cy);
            Mouse(EventKind.MouseButtonDown, cx, cy);
            Mouse(EventKind.MouseButtonUp, cx, cy);
            Check(EditorHost.Selection(id) == Word, "a double click selected '" + EditorHost.Selection(id) + "'");
            Mouse(EventKind.MouseButtonDown, cx, cy);
            Mouse(EventKind.MouseButtonUp, cx, cy);
            var line = "// " + string.Join(" ", new[] { Word, Word, Word, Word, Word, Word, Word, Word });
            var s = EditorHost.Selection(id);
            Check(s == line || s == line + "\n", "a triple click selected '" + s + "'");
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

    static void Mouse(EventKind kind, float x, float y)
    {
        Lider.OnEvent(new EventData { Kind = kind, Button = 1, X = x, Y = y });
    }

    static void Check(bool cond, string message)
    {
        if (cond || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}
