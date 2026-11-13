// ../TestFields.cs が lider で開くゲーム。フィールドの行に出す static と個体を持つ。
// Depot.Ticks は毎 frame 変わる。Depot.Motes と Depot.Calm は、決めた frame で次の順に変わる。
//   - 最初の frame: Hp が 10、11、12 の 3 個を足す
//   - ShiftFrame: 先頭 (Hp 10) を外す
//   - DropFrame: 2 つ目 (Hp 12) を外す
//   - AddFrame: Hp 20 を足す
//   - CalmFrame: Calm を 1 にする
//   - ManyFrame: Hp 30 から 33 の 4 個を足す
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
