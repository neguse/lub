// ../TestReloadNotes.cs が lider で開くゲーム。Target.cs の個体を 1 つ持つ。
public static class Game
{
    public static ReloadNotesTarget Target = new ReloadNotesTarget();
    public static int Twice;

    public static void OnFrame(float dt)
    {
        Twice = Target.Twice();
    }
}
