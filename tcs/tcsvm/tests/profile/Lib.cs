// --trace を付けないファイル (中の時間は、これを呼んだ Game の行に付く)
public static class Lib
{
    public static void Work()
    {
        Probe.Clock.Advance(7);
    }
}
