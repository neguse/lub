// ../TestInspector.cs が lider で開くゲーム。インスペクタに出す class と、通るメソッドと通らないメソッドを持つ。
// 2 つ目の Crate だけが毎 frame Hit を通る。
// RemoveFrame 個目の frame で最初の Crate を外し、StopFrame 個目の frame から Tick を通らない。
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
