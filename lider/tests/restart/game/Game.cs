// ../TestRestart.cs が lider で開くゲーム。毎 frame 数を 1 増やす。
public static class Game
{
    public static int Count;

    public static void OnFrame(float dt)
    {
        Count++;
    }
}
