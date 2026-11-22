// The game ../TestCompare.cs opens in lider. Increases the count by Speed.Of() every frame.
public static class Game
{
    public static int Count;

    public static void OnFrame(float dt)
    {
        Count += Speed.Of();
    }
}
