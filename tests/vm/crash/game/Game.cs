// 落ちたとき (SIGSEGV)、player が走らせていた callback と C# の呼び出しの段を出すことの検査。
// 期待する出力は ../expect-crash.txt (scripts/vm-project-test.sh が照らし合わせる)
using Lub;

public static class Game
{
    static int frames;

    public static void OnInit() { App.Config(new ConfigOpts { Width = 64, Height = 64 }); }

    public static void OnFrame(float dt)
    {
        frames++;
        if (frames == 3)
            Fall();
    }

    static void Fall()
    {
        Crash.Now();
    }
}
