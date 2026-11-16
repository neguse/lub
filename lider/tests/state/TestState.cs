// ゲームの状態を、名前を付けて控えて戻す (state:save、state:load:<名前>)。
//   - 控えた時点に戻り、そこから動き続ける
//   - 控えた後に class に field を足して保存しても、その控えに戻せる (前の field は控えの値、足した field は初期化子の値)
//   - 止めている間に戻すと、戻した状態から動き続ける
using System;
using Lub;
using LiderCore;

public static class TestState
{
    static int stage;
    static int frames;
    static int savedCount;
    static int savedA;
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
            Lider.SaveState("a");
            Check(Lider.StateNotice == "", "saving the state failed: " + Lider.StateNotice);
            savedCount = Read("Game", "Count");
            savedA = Read("Game.T", "A");
            Check(savedCount > 0 && savedA > 5, "cannot read the game: Count " + savedCount + " A " + savedA);
            frames = 0;
            stage = 1;
        }
        else if (stage == 1 && frames == 20)
        {
            Check(Read("Game", "Count") >= savedCount + 15, "the game did not go on after saving: " + Read("Game", "Count"));
            Lider.Run("state:load:a");
            Check(Lider.StateNotice == "", "loading the state failed: " + Lider.StateNotice);
            frames = 0;
            stage = 2;
        }
        else if (stage == 2 && frames == 3)
        {
            // 控えた時点に戻り、そこから動き続ける (戻してから 3 frame ほど)
            Back("after loading");
            // 控えた後に、class に field を足して保存する
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
            var doc = w.Focus!;
            Check(doc.Find("public int A = 5;"), "no field to edit");
            doc.Type("public int B = 7;\n    ");
            doc.Save();
            stage = 3;
        }
        else if (stage == 3 && Read("Game.T", "B") == 7)
        {
            frames = 0;
            stage = 4;
        }
        else if (stage == 4 && frames == 20)
        {
            Lider.Run("state:load:a");
            Check(Lider.StateNotice == "", "loading the state into the edited version failed: " + Lider.StateNotice);
            frames = 0;
            stage = 5;
        }
        else if (stage == 5 && frames == 3)
        {
            Back("after loading into the edited version");
            Check(Read("Game.T", "B") == 7, "the added field is " + Read("Game.T", "B") + ", want its initializer 7");
            // 止めている間に戻す
            Lider.Run("game:pause");
            frames = 0;
            stage = 6;
        }
        else if (stage == 6 && frames == 10)
        {
            Check(GameApp.Position >= 0, "the game is not paused");
            Lider.Run("state:load:a");
            frames = 0;
            stage = 7;
        }
        else if (stage == 7 && frames == 3)
        {
            Check(GameApp.Position < 0, "the game is still paused after loading a state");
            Back("after loading while paused");
            Console.WriteLine("PASS");
            App.Quit(0);
        }
    }

    // 控えた時点の少し後に居るか (戻してから数 frame)
    static void Back(string when)
    {
        var count = Read("Game", "Count");
        var a = Read("Game.T", "A");
        Check(count >= savedCount && count <= savedCount + 6, when + ": Count " + count + ", saved at " + savedCount);
        Check(a >= savedA && a <= savedA + 6, when + ": A " + a + ", saved at " + savedA);
    }

    static int Read(string path, string name)
    {
        foreach (var n in GameApp.Inspect(path, 0, 100))
            if (n.Name == name && int.TryParse(n.Text, out var value))
                return value;
        return -1;
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
