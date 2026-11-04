using System.Collections.Generic;
using Lub;

public struct Pos
{
    public float X;
    public float Y;
}

public class Player
{
    public int Hp = 3;
    public Pos At;
    public List<int> Items = new List<int>();
}

public static class Game
{
    public static int Score;
    public static Player P = new Player();
    public static Dictionary<string, int> Counts = new Dictionary<string, int>();

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
        P.Items.Add(7);
        Counts["a"] = 1;
    }

    public static void OnFrame(float dt)
    {
        Score = Score + 1;
        P.At.X = Score * 2;
        int doubled = Score * 2;
        P.Hp = doubled;
    }

    // native の値 (Bindings は型の印の無い block を field に持つ) も、検査の口で覗ける
    public static Bindings B = MakeBindings();

    static Bindings MakeBindings()
    {
        var b = new Bindings();
        b.Set("u", new List<float> { 1, 2, 3, 4 });
        return b;
    }
}
