using System.Collections.Generic;
using Lub;

public static class Game
{
    public static List<int>? Keep;
    public static int[]? Big;
    static int sum;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        Keep = new List<int> { 1, 2, 3 };
        for (int i = 0; i < 200000; i++)
            sum += i;
        Big = new int[100000];
    }
}
