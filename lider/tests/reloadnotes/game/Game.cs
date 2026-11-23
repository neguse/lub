// The game ../TestReloadNotes.cs opens in lider. Holds one instance of Target.cs.
public static class Game
{
    public static ReloadNotesTarget Target = new ReloadNotesTarget();
    public static int Twice;

    public static void OnFrame(float dt)
    {
        Twice = Target.Twice();
    }
}
