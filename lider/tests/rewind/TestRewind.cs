// lider でゲームを止め、1 frame ずつ戻り、進み、そこから続けることを確かめる。
// ゲーム (game/Game.cs) は毎 frame 数を 1 増やす。数は lider がゲームを見るのと同じ道 (GameApp.Inspect) で読む。
// 止めている間は、止めた frame の状態から作り直すので、数は止めた frame の値のまま。
// key は lub から届く event と同じに Lider.OnEvent へ流し、次も確かめる。
//   - F5 を押しっぱなしにして繰り返しの KeyDown が届いても、止めるのは 1 度だけ
//   - 止めている間は空間が冷える (Lider.Cold が 0 より上がる)
//   - F6 で戻り、F7 で進む。一番古い frame で F6 を押しても動かない
//   - 一番新しい frame で F7 を押すと、ゲームを 1 frame 動かして控えに足し、そこで止める
//   - lider を隠している間の F5 と、控えが無いときの F5 では止めない
//   - LIDER_REWIND に 0 を渡すと控えない
// 確かめるのは lider がゲームを進めた後 (Lider.OnFrame の後)。frame はゲームが動き始めてから数える。
using System;
using Lub;
using LiderCore;

public static class TestRewind
{
    static int frames;
    static int at;
    static bool failed;
    static int kept;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
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
        if (GameApp.Frames == 0 || failed)
            return;
        Check();
    }

    // ゲームの Count の今の値 (読めなければ -1)
    static int Count
    {
        get
        {
            foreach (var n in GameApp.Inspect("Game", 0, 100))
                if (n.Name == "Count" && int.TryParse(n.Text, out var count))
                    return count;
            return -1;
        }
    }

    static void Check()
    {
        frames++;
        var fail = "";
        if (frames == 20)
        {
            at = Count;
            Down(Keys.F5);
            Down(Keys.F5);
            Up(Keys.F5);
        }
        else if (frames == 22)
        {
            if (Count != at || GameApp.Position != 0)
                fail = "paused: count " + Count + " at " + GameApp.Position + ", want " + at + " at 0";
            kept = GameApp.Count;
            Press(Keys.F7);
        }
        else if (frames == 24)
        {
            if (Count != at + 1 || GameApp.Position != 0 || GameApp.Count != kept + 1)
                fail = "F7 on the newest frame: count " + Count + " at " + GameApp.Position + " of " + GameApp.Count
                    + ", want " + (at + 1) + " at 0 of " + (kept + 1);
            at = Count;
            Press(Keys.F6);
        }
        else if (frames == 26)
        {
            if (Lider.Cold <= 0)
                fail = "the space did not cool while paused";
            if (fail == "" && (Count != at - 1 || GameApp.Position != 1))
                fail = "one back: count " + Count + " at " + GameApp.Position + ", want " + (at - 1) + " at 1";
            Press(Keys.F7);
        }
        else if (frames == 28)
        {
            if (Count != at || GameApp.Position != 0)
                fail = "forward again: count " + Count + " at " + GameApp.Position + ", want " + at + " at 0";
            Press(Keys.F5);
        }
        else if (frames == 30)
        {
            if (GameApp.Position != -1 || Count != at + 1)
                fail = "resumed: count " + Count + " at " + GameApp.Position + ", want " + (at + 1) + " at -1";
            Lider.SetActive(false);
            Press(Keys.F5);
            if (fail == "" && GameApp.Position != -1)
                fail = "F5 while lider was hidden paused the game";
            Lider.SetActive(true);
            Press(Keys.F5);
            at = Count;
            for (var i = 0; i < GameApp.Count + 2; i++)
                Press(Keys.F6);
            if (fail == "" && GameApp.Position != GameApp.Count - 1)
                fail = "F6 past the oldest frame moved to " + GameApp.Position + ", want " + (GameApp.Count - 1);
        }
        else if (frames == 32)
        {
            if (Count != at - GameApp.Position)
                fail = "oldest: count " + Count + ", want " + (at - GameApp.Position);
            Lider.Record("0");
            if (fail == "" && GameApp.Count != 0)
                fail = "LIDER_REWIND=0 kept " + GameApp.Count + " frames";
            Press(Keys.F5);
            if (fail == "" && GameApp.Position != -1)
                fail = "F5 with no frames paused at " + GameApp.Position;
            if (fail == "")
            {
                Console.WriteLine("PASS");
                App.Quit(0);
            }
        }
        if (fail != "")
        {
            failed = true;
            Console.WriteLine("FAIL " + fail);
            App.Quit(1);
        }
    }

    static void Down(int key)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = 0, Typed = Keys.UsChar(key, false), Plain = Keys.UsChar(key, false) });
    }

    static void Up(int key)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = 0, Typed = Keys.UsChar(key, false), Plain = Keys.UsChar(key, false) });
    }

    static void Press(int key)
    {
        Down(key);
        Up(key);
    }
}
