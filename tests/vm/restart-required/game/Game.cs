// Checks a save that a diff cannot carry over: changing the value of an enum member and saving makes tcs signal a restart, and the player recreates the VM and starts from OnInit.
// The statics of the recreated VM get the values of the new initializers.
// The player logs `lub: restarted` each time it recreates the VM (../expect.txt).
using System;
using Lub;

public enum Phase
{
    First,
    Second,
}

public static class Game
{
    static int Version = 1;
    static int frames;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
        if (Version == 2)
        {
            Console.WriteLine("PASS");
            App.Quit(0);
        }
    }

    public static void OnFrame(float dt)
    {
        frames++;
        if (frames == 3)
        {
            // The strings to replace are written split up, so this line is not rewritten
            string path = Environment.GetEnvironmentVariable("GAME_CS") ?? "";
            Io.LoadText(path, out var text, out _, out _, out _);
            if (text == null)
            {
                Console.WriteLine("FAIL cannot read " + path);
                App.Quit(1);
                return;
            }
            text = text.Replace("Version = " + "1;", "Version = " + "2;");
            Io.SaveText(path, text.Replace("    First,\n" + "    Second,", "    Second,\n" + "    First,"));
        }
        if (Sys.Seconds() > 60)
        {
            Console.WriteLine("FAIL the enum change did not restart the VM");
            App.Quit(1);
        }
    }
}
