using Lub;

public static class Game
{
    public static bool Inited;
    public static int Frames;
    public static int Step = 1;
    public static int Reloads;
    public static string Failed = "";

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
        Inited = true;
    }

    public static void OnFrame(float dt)
    {
        Frames += Step;
    }

    public static void OnReload() { Reloads++; }

    public static void OnBuildFailed(string errors) { Failed = errors; }
}
