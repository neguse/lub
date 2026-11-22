// The game ../TestRestart.cs opens in lider. Increases the count by 1 every frame.
public static class Game
{
    public static int Count;

    public static void OnFrame(float dt)
    {
        Count++;
    }
}
