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
}
