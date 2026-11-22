// The game ../TestValues.cs opens in lider. Increases Count and Thing's A by 1 every frame. Does not change Score and Flag itself.
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
        // An unread static does not go into the module
        if (Flag && Score < 0)
            Count = 0;
    }
}
