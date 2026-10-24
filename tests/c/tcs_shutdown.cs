using System;
using Lub;

// web の tcs2c の player を page が外すとき (Module._lub_tcs_shutdown) の検査に使うゲーム。
// web/scripts/tcs-shutdown-check.mjs が log の行を見る。
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
