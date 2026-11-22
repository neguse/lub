// Checks that on a crash (SIGSEGV), the player prints the callback it was running and the frames of the C# call stack.
// The expected output is ../expect-crash.txt (scripts/vm-project-test.sh compares them)
using Lub;

public static class Game
{
    static int frames;

    public static void OnInit() { App.Config(new ConfigOpts { Width = 64, Height = 64 }); }

    public static void OnFrame(float dt)
    {
        frames++;
        if (frames == 3)
            Fall();
    }

    static void Fall()
    {
        Crash.Now();
    }
}
