// The file tests/diffinline opens and edits.
public static class DiffInlineTarget
{
    public static int Hp;

    public static void Step()
    {
        Hp = 1;
        Hp = 2;
    }
}
