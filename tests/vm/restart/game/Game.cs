// Checks App.Restart: calling it in the 3rd frame recreates the VM at the end of the frame and starts from OnInit, and statics return to their initial values.
// Objects returned by lub can be created even in the recreated VM (the type cache does not use entries from the previous VM).
// The marker that survives the recreation is written to a file next to the game (Game.cs.restarted)
using System;
using Lub;

public static class Game
{
    static int frames;
    static bool second;

    static string Mark()
    {
        return (Environment.GetEnvironmentVariable("GAME_CS") ?? "Game.cs") + ".restarted";
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
        Io.LoadText(Mark(), out var text, out _, out _, out _);
        second = text == "1";
        if (second && frames != 0)
        {
            Console.WriteLine("FAIL frames was " + frames + " after the restart, want 0");
            App.Quit(1);
        }
    }

    public static void OnFrame(float dt)
    {
        frames++;
        // Used from before the recreation, to fill the lub type cache
        Sys.TakeTrace();
        if (!second && frames == 3)
        {
            Io.SaveText(Mark(), "1");
            App.Restart();
        }
        if (second && frames == 3)
        {
            // Even in the recreated VM, the type of the object that lub creates and returns (here a List) is looked up again
            if (Sys.TakeTrace() == null)
            {
                Console.WriteLine("FAIL a list from lub was null after the restart");
                App.Quit(1);
                return;
            }
            Console.WriteLine("PASS");
            App.Quit(0);
        }
        if (frames == 30)
        {
            Console.WriteLine("FAIL App.Restart did not restart");
            App.Quit(1);
        }
    }
}
