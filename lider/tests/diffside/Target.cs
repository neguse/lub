// tests/diffside で開いて書き換える file。
public static class DiffSideTarget
{
    public static int Hp;

    public static void Step()
    {
        Hp = 1;
        Hp = 2;
    }
}
