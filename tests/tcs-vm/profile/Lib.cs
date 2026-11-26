// A file without --trace (time spent inside it is attributed to the Game line that called it)
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
