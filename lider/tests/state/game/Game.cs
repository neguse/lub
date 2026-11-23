// The game ../TestState.cs opens in lider. Increases the count by 1 every frame, and also increases the value of Thing (Target.cs) by 1.
public static class Game
{
    public static int Count;
    public static Thing T = new Thing();

    public static void OnFrame(float dt)
    {
        Count++;
        T.A++;
    }
}
