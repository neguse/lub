// App.Restart の検査: 3 frame 目に呼ぶと、frame の終わりに VM を作り直して OnInit から始め、static は初期値に戻る。
// 作り直した VM でも、lub が返す object を作れる (型の cache が前の VM の物を使わない)。
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
        // 作り直す前から使い、lub の型の cache を埋めておく
        Sys.TakeTrace();
        if (!second && frames == 3)
        {
            Io.SaveText(Mark(), "1");
            App.Restart();
        }
        if (second && frames == 3)
        {
            // 作り直した VM でも、lub が作って返す object (ここでは List) の型を引き直している
            if (Sys.TakeTrace() == null)
            {
                Console.WriteLine("FAIL a list from lub was null after the restart");
                App.Quit(1);
                return;
            }
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
