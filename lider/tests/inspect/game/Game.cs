// ../TestInspect.cs が lider で開くゲーム。インスペクタに出す並びと値を持つ。
// Shelf.Ticks は毎 frame 変わり、Shelf.Calm は CalmFrame 個目の frame で 1 度だけ変わる。
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
