using Lub;

public class Tree
{
    public int Hp = 40;
}

public static class Game
{
    public static bool Inited;
    public static int Frames;
    public static int Step = 1;
    public static int Reloads;
    public static string Failed = "";
    public static string Notes = "";
    public static int ReloadsAtNotes;
    public static Tree Planted = new Tree();

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

    public static void OnReloadNotes(string notes)
    {
        Notes = notes;
        ReloadsAtNotes = Reloads;
    }
}
