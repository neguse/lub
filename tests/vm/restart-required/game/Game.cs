// 差分で移せない保存の検査: static の初期化子を変えて保存すると、tcs が restart を知らせ、player は VM を作り直して OnInit から始める。
// 作り直した VM の static は新しい初期化子の値になる。
// player は作り直すたびに `lub: restarted` を log に出す (../expect.txt)。
using System;
using Lub;

public static class Game
{
    static int Version = 1;
    static int frames;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
        if (Version == 2)
        {
            Console.WriteLine("PASS");
            App.Quit(0);
        }
    }

    public static void OnFrame(float dt)
    {
        frames++;
        if (frames == 3)
        {
            // 置き換える文字列は分けて書き、この行は書き換えない
            string path = Environment.GetEnvironmentVariable("GAME_CS") ?? "";
            Io.LoadText(path, out var text, out _, out _, out _);
            if (text == null)
            {
                Console.WriteLine("FAIL cannot read " + path);
                App.Quit(1);
                return;
            }
            Io.SaveText(path, text.Replace("Version = " + "1;", "Version = " + "2;"));
        }
        if (Sys.Seconds() > 60)
        {
            Console.WriteLine("FAIL the static initializer change did not restart the VM");
            App.Quit(1);
        }
    }
}
