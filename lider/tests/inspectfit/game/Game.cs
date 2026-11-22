// The game ../TestInspectFit.cs opens in lider. Holds one value to show in the inspector.
public static class Stage
{
    public static int ClearTicks = 4980;
}

public static class Game
{
    public static void OnFrame(float dt)
    {
        Stage.ClearTicks = 4980;
    }
}
