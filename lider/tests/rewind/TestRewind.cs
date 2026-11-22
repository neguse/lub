// Checks that lider can pause the game, step back and forward 1 frame at a time, and continue from there.
// The game (game/Game.cs) increments a count by 1 every frame. The count is read through the same path lider uses to look at the game (GameApp.Inspect).
// While paused, the game is recreated from the state of the paused frame, so the count stays at the value of the paused frame.
// Keys are fed to Lider.OnEvent, the same as events that come from lub, and the following are also checked.
//   - Even if F5 is held down and repeated KeyDowns arrive, it pauses only once
//   - While paused, the space cools down (Lider.Cold rises above 0)
//   - F6 goes back and F7 goes forward. Pressing F6 at the oldest frame does not move
//   - Pressing F7 at the newest frame runs the game for 1 frame, adds it to the recorded frames, and pauses there
//   - F5 while lider is hidden, and F5 when nothing is recorded, do not pause
//   - Passing 0 in LIDER_REWIND turns recording off
// Checks run after lider advances the game (after Lider.OnFrame). Frames are counted from when the game starts running.
using System;
using Lub;
using LiderCore;

public static class TestRewind
{
    static int frames;
    static int at;
    static bool failed;
    static int kept;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
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
        if (GameApp.Frames == 0 || failed)
            return;
        Check();
    }

    // Current value of the game's Count (-1 if it cannot be read)
    static int Count
    {
        get
        {
            foreach (var n in GameApp.Inspect("Game", 0, 100))
                if (n.Name == "Count" && int.TryParse(n.Text, out var count))
                    return count;
            return -1;
        }
    }

    static void Check()
    {
        frames++;
        var fail = "";
        if (frames == 20)
        {
            at = Count;
            Down(Keys.F5);
            Down(Keys.F5);
            Up(Keys.F5);
        }
        else if (frames == 22)
        {
            if (Count != at || GameApp.Position != 0)
                fail = "paused: count " + Count + " at " + GameApp.Position + ", want " + at + " at 0";
            kept = GameApp.Count;
            Press(Keys.F7);
        }
        else if (frames == 24)
        {
            if (Count != at + 1 || GameApp.Position != 0 || GameApp.Count != kept + 1)
                fail = "F7 on the newest frame: count " + Count + " at " + GameApp.Position + " of " + GameApp.Count
                    + ", want " + (at + 1) + " at 0 of " + (kept + 1);
            at = Count;
            Press(Keys.F6);
        }
        else if (frames == 26)
        {
            if (Lider.Cold <= 0)
                fail = "the space did not cool while paused";
            // While paused, the band of recorded frames appears at the bottom edge of the game view
            Gfx.Size(out var sw, out var sh);
            var band = Lider.TimelineViewport();
            if (fail == "" && (band.Z <= band.X || band.W > sh || band.Y < sh - 40))
                fail = "the timeline is not at the bottom of the game: " + band.X + "," + band.Y + " - " + band.Z + "," + band.W;
            if (fail == "" && (Count != at - 1 || GameApp.Position != 1))
                fail = "one back: count " + Count + " at " + GameApp.Position + ", want " + (at - 1) + " at 1";
            Press(Keys.F7);
        }
        else if (frames == 28)
        {
            if (Count != at || GameApp.Position != 0)
                fail = "forward again: count " + Count + " at " + GameApp.Position + ", want " + at + " at 0";
            Press(Keys.F5);
        }
        else if (frames == 30)
        {
            if (GameApp.Position != -1 || Count != at + 1)
                fail = "resumed: count " + Count + " at " + GameApp.Position + ", want " + (at + 1) + " at -1";
            if (fail == "" && Lider.TimelineViewport().Z != 0)
                fail = "the timeline stayed after resuming";
            Lider.FocusGame();
            Press(Keys.F5);
            if (fail == "" && GameApp.Position != -1)
                fail = "F5 while lider was hidden paused the game";
            Lider.FocusCode();
            Press(Keys.F5);
            at = Count;
            for (var i = 0; i < GameApp.Count + 2; i++)
                Press(Keys.F6);
            if (fail == "" && GameApp.Position != GameApp.Count - 1)
                fail = "F6 past the oldest frame moved to " + GameApp.Position + ", want " + (GameApp.Count - 1);
        }
        else if (frames == 32)
        {
            if (Count != at - GameApp.Position)
                fail = "oldest: count " + Count + ", want " + (at - GameApp.Position);
            Lider.Record("0");
            if (fail == "" && GameApp.Count != 0)
                fail = "LIDER_REWIND=0 kept " + GameApp.Count + " frames";
            Press(Keys.F5);
            if (fail == "" && GameApp.Position != -1)
                fail = "F5 with no frames paused at " + GameApp.Position;
            if (fail == "")
            {
                Console.WriteLine("PASS");
                App.Quit(0);
            }
        }
        if (fail != "")
        {
            failed = true;
            Console.WriteLine("FAIL " + fail);
            App.Quit(1);
        }
    }

    static void Down(int key)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = 0, Typed = Keys.UsChar(key, false), Plain = Keys.UsChar(key, false) });
    }

    static void Up(int key)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = 0, Typed = Keys.UsChar(key, false), Plain = Keys.UsChar(key, false) });
    }

    static void Press(int key)
    {
        Down(key);
        Up(key);
    }
}
