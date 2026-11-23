// The file tests/diffside opens and edits.
public static class DiffSideTarget
{
    public static int Hp;

    public static void Step()
    {
        Hp = 1;
        Hp = 2;
    }
}
