// ../TestBuildError.cs が lider で開くゲーム。毎 frame Target.cs の値を読む。
public static class Game
{
    public static int Value;

    public static void OnFrame(float dt)
    {
        Value = BuildErrorTarget.Value();
    }
}
