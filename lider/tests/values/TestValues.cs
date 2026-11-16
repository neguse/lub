// 止めている間に、ゲームの値を書き換える (Lider.SetValue、Lider.EditValue)。
//   - 書いた値は、止めた frame を進める前の状態に入り、その frame を作り直す (frame が変えない値はそのまま見え、frame が変える値は 1 frame 分だけ進んで見える)
//   - bool は、押すだけで反転する
//   - 値として読めない文字は書けず、理由が知らせに出て、値は変わらない
//   - 続けると、書いた値から動く
//   - 動いている間は書けない
using System;
using Lub;
using LiderCore;

public static class TestValues
{
    static int stage;
    static int frames;
    static int count;
    static bool failed;

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
        if (failed || GameApp.Frames < 20)
            return;
        frames++;
        Check(frames < 3000, "the test did not finish (stage " + stage + ")");
        var w = Lider.Work!;
        if (stage == 0)
        {
            Lider.SetValue("Game.Score", "7");
            Check(Lider.StateNotice != "" && Read("Game", "Score") == 0, "a value was written while the game runs: " + Read("Game", "Score"));
            Lider.Run("game:pause");
            frames = 0;
            stage = 1;
        }
        else if (stage == 1 && frames == 5)
        {
            Check(GameApp.Position >= 0, "the game is not paused");
            count = Read("Game", "Count");
            Lider.SetValue("Game.Score", "42");
            Check(Lider.StateNotice == "", "writing Score failed: " + Lider.StateNotice);
            Lider.SetValue("Game.T.A", "100");
            Check(Lider.StateNotice == "", "writing T.A failed: " + Lider.StateNotice);
            Lider.EditValue(w, "Game.Flag", "false");
            Check(Lider.StateNotice == "", "flipping Flag failed: " + Lider.StateNotice);
            frames = 0;
            stage = 2;
        }
        else if (stage == 2 && frames == 5)
        {
            Check(GameApp.Position == 0, "the game is not paused at the written frame: " + GameApp.Position);
            Check(Read("Game", "Count") == count, "writing a value moved the frame: Count " + Read("Game", "Count") + ", was " + count);
            Check(Read("Game", "Score") == 42, "Score is " + Read("Game", "Score"));
            // T.A は frame が 1 増やすので、書いた 100 から 1 frame 進んだ値が見える
            Check(Read("Game.T", "A") == 101, "T.A is " + Read("Game.T", "A"));
            Check(Text("Game", "Flag") == "true", "Flag is " + Text("Game", "Flag"));
            Lider.SetValue("Game.Score", "many");
            Check(Lider.StateNotice.Contains("cannot read"), "a bad value was not refused: " + Lider.StateNotice);
            frames = 0;
            stage = 3;
        }
        else if (stage == 3 && frames == 3)
        {
            Check(Read("Game", "Score") == 42, "a refused write changed Score: " + Read("Game", "Score"));
            Lider.Run("game:pause");
            frames = 0;
            stage = 4;
        }
        else if (stage == 4 && frames == 10)
        {
            Check(GameApp.Position < 0, "the game did not resume");
            Check(Read("Game", "Count") > count + 5, "the game did not go on: Count " + Read("Game", "Count"));
            Check(Read("Game", "Score") == 42 && Read("Game.T", "A") > 105, "the game did not go on from the written values: Score " + Read("Game", "Score") + " A " + Read("Game.T", "A"));
            Console.WriteLine("PASS");
            App.Quit(0);
        }
    }

    static string Text(string path, string name)
    {
        foreach (var n in GameApp.Inspect(path, 0, 100))
            if (n.Name == name)
                return n.Text;
        return "";
    }

    static int Read(string path, string name)
    {
        return int.TryParse(Text(path, name), out var value) ? value : -1;
    }

    static void Check(bool ok, string message)
    {
        if (ok || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}
