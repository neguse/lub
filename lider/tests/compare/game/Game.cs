// ../TestCompare.cs が lider で開くゲーム。毎 frame 数を Speed.Of() だけ増やす。
public static class Game
{
    public static int Count;

    public static void OnFrame(float dt)
    {
        Count += Speed.Of();
    }
}
