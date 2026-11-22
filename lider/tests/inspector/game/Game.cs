// The game ../TestInspector.cs opens in lider. Holds a class to show in the inspector, with a method that runs and a method that does not.
// Only the second Crate runs Hit every frame.
// Removes the first Crate on the RemoveFrame-th frame, and stops running Tick from the StopFrame-th frame.
using System.Collections.Generic;

public class Crate
{
    public int Hp;
    public string Name = "";

    public void Hit()
    {
        Hp = Hp - 1;
    }
}

public static class Busy
{
    public static int Count;
    public static bool Stop;
    public static List<Crate> Crates = new List<Crate>();

    public static void Tick()
    {
        Count = Count + 1;
    }

    public static void Never()
    {
        Count = 0;
    }
}

public static class Game
{
    const int RemoveFrame = 80;
    const int StopFrame = 140;
    static int frames;

    public static void OnInit()
    {
        Busy.Crates.Add(new Crate { Hp = 3, Name = "a" });
        Busy.Crates.Add(new Crate { Hp = 5, Name = "b" });
    }

    public static void OnFrame(float dt)
    {
        frames++;
        if (frames == RemoveFrame)
            Busy.Crates.RemoveAt(0);
        if (frames == StopFrame)
            Busy.Stop = true;
        if (!Busy.Stop)
            Busy.Tick();
        if (Busy.Crates.Count > 1)
            Busy.Crates[1].Hit();
    }
}
