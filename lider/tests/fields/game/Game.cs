// The game ../TestFields.cs opens in lider. Holds statics and instances to show on the field lines.
// Depot.Ticks changes every frame. Depot.Motes and Depot.Calm change on set frames in the following order.
//   - first frame: add 3 instances with Hp 10, 11, 12
//   - ShiftFrame: remove the first (Hp 10)
//   - DropFrame: remove the second (Hp 12)
//   - AddFrame: add Hp 20
//   - CalmFrame: set Calm to 1
//   - ManyFrame: add 4 instances with Hp 30 to 33
using System.Collections.Generic;

public class Mote
{
    public int Hp;
    public string Name = "x";
}

public class Solo
{
    public int Power = 7;
}

public static class Depot
{
    public const int Limit = 5;
    public static int Calm;
    public static int Ticks;
    public static Solo One = new Solo();
    public static List<Mote> Motes = new List<Mote>();
}

public static class Game
{
    const int ShiftFrame = 40;
    const int DropFrame = 80;
    const int AddFrame = 120;
    const int CalmFrame = 200;
    const int ManyFrame = 240;
    static int frames;

    public static void OnFrame(float dt)
    {
        frames++;
        Depot.Ticks = frames;
        if (frames == 1)
            for (var i = 0; i < 3; i++)
                Depot.Motes.Add(new Mote { Hp = 10 + i });
        if (frames == ShiftFrame)
            Depot.Motes.RemoveAt(0);
        if (frames == DropFrame)
            Depot.Motes.RemoveAt(1);
        if (frames == AddFrame)
            Depot.Motes.Add(new Mote { Hp = 20 });
        if (frames == CalmFrame)
            Depot.Calm = 1;
        if (frames == ManyFrame)
            for (var i = 0; i < 4; i++)
                Depot.Motes.Add(new Mote { Hp = 30 + i });
    }
}
