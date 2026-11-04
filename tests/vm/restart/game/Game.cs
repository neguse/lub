// App.Restart の検査: 3 frame 目に呼ぶと、frame の終わりに VM を作り直して OnInit から始め、static は初期値に戻る。
// 作り直しを跨いで残す印は、ゲームの隣の file (Game.cs.restarted) に書く
using System;
using Lub;

public static class Game
{
    static int frames;
    static bool second;

    static string Mark()
    {
        return (Environment.GetEnvironmentVariable("GAME_CS") ?? "Game.cs") + ".restarted";
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
        Io.LoadText(Mark(), out var text, out _, out _, out _);
        second = text == "1";
        if (second && frames != 0)
        {
            Console.WriteLine("FAIL frames was " + frames + " after the restart, want 0");
            App.Quit(1);
        }
    }

    public static void OnFrame(float dt)
    {
        frames++;
        if (!second && frames == 3)
        {
            Io.SaveText(Mark(), "1");
            App.Restart();
        }
        if (second && frames == 3)
        {
            Console.WriteLine("PASS");
            App.Quit(0);
        }
        if (frames == 30)
        {
            Console.WriteLine("FAIL App.Restart did not restart");
            App.Quit(1);
        }
    }
}
