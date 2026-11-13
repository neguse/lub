// ../TestRelayout.cs が lider で開くゲーム。毎 frame 数を 1 増やし、Target.cs の値を読む。
public static class Game
{
    public static int Count;
    public static int Seen;

    public static void OnFrame(float dt)
    {
        Count++;
        Seen = Target.Value;
    }
}
