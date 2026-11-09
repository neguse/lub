// --trace を付けないファイル (中の時間は、これを呼んだ Game の行に付く)
public static class Lib
{
    public static void Work()
    {
        Probe.Clock.Advance(7);
    }

    static int[]? spent;

    public static void Spend(int n)
    {
        Probe.Clock.Advance(9);
        spent = new int[n];
    }
}
