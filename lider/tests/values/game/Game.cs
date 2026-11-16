// ../TestValues.cs が lider で開くゲーム。毎 frame Count と Thing の A を 1 増やす。Score と Flag は自分では変えない。
public class Thing
{
    public int A = 5;
}

public static class Game
{
    public static int Count;
    public static int Score;
    public static bool Flag;
    public static Thing T = new Thing();

    public static void OnFrame(float dt)
    {
        Count++;
        T.A++;
        // 読まない static は module に入らない
        if (Flag && Score < 0)
            Count = 0;
    }
}
