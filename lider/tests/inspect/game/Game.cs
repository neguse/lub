// The game ../TestInspect.cs opens in lider. Holds the lists and values to show in the inspector.
// Shelf.Ticks changes every frame, and Shelf.Calm changes only once, on the CalmFrame-th frame.
using System.Collections.Generic;

public class Crate
{
    public int Hp;
    public string Name = "";
    public Crate? Next;
}

public static class Shelf
{
    public static List<Crate> Crates = new List<Crate>();
    public static List<int> Many = new List<int>();
    public static int Ticks;
    public static int Calm;
}

public static class Game
{
    const int CalmFrame = 60;
    static int frames;

    public static void OnFrame(float dt)
    {
        frames++;
        Shelf.Ticks = frames;
        if (frames == 1)
        {
            var a = new Crate { Hp = 3, Name = "a" };
            a.Next = new Crate { Hp = 5, Name = "b" };
            Shelf.Crates.Add(a);
            Shelf.Crates.Add(a.Next);
            Shelf.Crates.Add(new Crate { Hp = 9 });
            for (var i = 0; i < 250; i++)
                Shelf.Many.Add(i * 2);
        }
        if (frames == CalmFrame)
            Shelf.Calm = 1;
    }
}
