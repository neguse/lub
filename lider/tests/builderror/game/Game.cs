// The game ../TestBuildError.cs opens in lider. Reads the value of Target.cs every frame.
public static class Game
{
    public static int Value;

    public static void OnFrame(float dt)
    {
        Value = BuildErrorTarget.Value();
    }
}
