// Shift+F5 でゲームを最初から作り直す (lub の App.Restart に開いたゲームを渡す)。
// 作り直すとゲームの static は初期値に戻る。lider は作り直されないので、開いていた窓は残る。
using System;
using Lub;
using LiderCore;

public static class TestRestart
{
    static int frames;
    static int before;
    static bool pressed;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
        Lider.OnInit();
    }

    public static void OnEvent(EventData e)
    {
        Lider.OnEvent(e);
    }

    public static void OnQuit()
    {
        Lider.OnQuit();
    }

    public static void OnFrame(float dt)
    {
        Lider.OnFrame(dt);
        // ゲームの build を待つ
        if (GameApp.Frames == 0)
            return;
        frames++;
        var w = Lider.Work!;
        if (frames == 1)
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_ROOT") + "/TestRestart.cs");
        if (!pressed && Count() >= 30)
        {
            before = Count();
            pressed = true;
            Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = Keys.F5, Mods = Keys.ModShift, Typed = 0, Plain = 0 });
            Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = Keys.F5, Mods = 0, Typed = 0, Plain = 0 });
            return;
        }
        if (pressed && Count() >= 0 && Count() < before)
        {
            if (w.Windows.Count != 1)
            {
                Console.WriteLine("FAIL the restart closed lider's window: " + w.Windows.Count + " windows");
                App.Quit(1);
                return;
            }
            Console.WriteLine("PASS");
            App.Quit(0);
        }
        if (frames == 600)
        {
            Console.WriteLine("FAIL Shift+F5 did not restart the game: count " + Count() + " after " + before);
            App.Quit(1);
        }
    }

    // ゲームの Count の今の値 (読めなければ -1)
    static int Count()
    {
        foreach (var n in GameApp.Inspect("Game", 0, 100))
            if (n.Name == "Count" && int.TryParse(n.Text, out var count))
                return count;
        return -1;
    }
}
