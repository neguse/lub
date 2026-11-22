using System;
using Lub;

// Game used to check what happens when a page unloads the web tcs2c player (Module._lub_tcs_shutdown).
// web/scripts/tcs-shutdown-check.mjs looks at the log lines.
static class ShutdownTest
{
    static int frames;

    public static void OnFrame(float dt)
    {
        frames++;
        Console.WriteLine("frame " + frames);
    }

    public static void OnQuit()
    {
        Console.WriteLine("quit after " + frames + " frames");
    }
}
