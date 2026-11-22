// The file tests/liveline opens and edits.
public static class LiveLineTarget
{
    public static int Hp;

    public static void Step()
    {
        Hp = 1;
        Hp = 2;
    }
}
