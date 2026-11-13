// ../TestInspectFit.cs が lider で開くゲーム。インスペクタに出す値を 1 つ持つ。
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
