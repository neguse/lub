// The game ../TestEdit.cs opens in lider. Increases the count by 1 every frame and reads the value of Target.cs.
public static class Game
{
    public static int Count;
    public static int Value;

    public static void OnFrame(float dt)
    {
        Count++;
        Value = Target.Value();
    }
}
