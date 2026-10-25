// TraceRefsGame の overlay。
using System;
using Lub;

public static class TraceRefsTool
{
    static int frames;
    static bool game;

    public static void OnFrame(float dt)
    {
        frames++;
        TraceRefsLib.Touch();
        foreach (var key in Sys.TakeTrace())
        {
            if (key.Contains("TraceRefsLib.cs") || key.Contains("TraceRefsTool.cs"))
            {
                Console.WriteLine("FAIL traced a source only the overlay reaches: " + key);
                App.Quit(1);
                return;
            }
            if (key.Contains("TraceRefsGame.cs"))
                game = true;
        }
        if (frames < 5)
            return;
        if (!game)
        {
            Console.WriteLine("FAIL the game's lines were not traced");
            App.Quit(1);
            return;
        }
        Console.WriteLine("PASS");
        App.Quit(0);
    }
}
