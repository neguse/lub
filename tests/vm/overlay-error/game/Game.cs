using Lub;

public static class Game
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64, ResourceSweepAfterFrames = 2 });
    }

    public static void OnFrame(float dt) { }
}
