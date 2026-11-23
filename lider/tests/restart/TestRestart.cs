// Shift+F5 recreates the game from the start (passes the opened game to lub's App.Restart).
// Recreating resets the game's statics to their initial values. lider is not recreated, so the open windows remain.
using System;
using Lub;
using LiderCore;

public static class TestRestart
{
    static int frames;
    static int before;
    static bool pressed;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
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
        Lider.OnFrame(dt);
        // Wait for the game build
        if (GameApp.Frames == 0)
            return;
        frames++;
        var w = Lider.Work!;
        if (frames == 1)
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_ROOT") + "/TestRestart.cs");
        if (!pressed && Count() >= 30)
        {
            before = Count();
            pressed = true;
            Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = Keys.F5, Mods = Keys.ModShift, Typed = 0, Plain = 0 });
            Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = Keys.F5, Mods = 0, Typed = 0, Plain = 0 });
            return;
        }
        if (pressed && Count() >= 0 && Count() < before)
        {
            if (w.Windows.Count != 1)
            {
                Console.WriteLine("FAIL the restart closed lider's window: " + w.Windows.Count + " windows");
                App.Quit(1);
                return;
            }
            Console.WriteLine("PASS");
            App.Quit(0);
        }
        if (frames == 600)
        {
            Console.WriteLine("FAIL Shift+F5 did not restart the game: count " + Count() + " after " + before);
            App.Quit(1);
        }
    }

    // Current value of the game's Count (-1 if it cannot be read)
    static int Count()
    {
        foreach (var n in GameApp.Inspect("Game", 0, 100))
            if (n.Name == "Count" && int.TryParse(n.Text, out var count))
                return count;
        return -1;
    }
}
