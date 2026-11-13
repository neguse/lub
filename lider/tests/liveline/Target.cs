// tests/liveline で開いて書き換える file。
public static class LiveLineTarget
{
    public static int Hp;

    public static void Step()
    {
        Hp = 1;
        Hp = 2;
    }
}
