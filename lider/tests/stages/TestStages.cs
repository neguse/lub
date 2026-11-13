// 編集、試す、ゲームだけの切り替え。
//   - F1 で試すに移る: 入力はゲームに渡り、窓は同じ所に薄く残り、compile error のような窓の中身は描き続ける。もう一度 F1 で濃く戻る
//   - 試す間は Zen の広げた窓を元の大きさにし、編集に戻ると広げ直す。検索は閉じる
//   - Shift+F1 でゲームだけ (窓を描かない) にし、もう一度で試すに戻る。ゲームだけから F1 で編集に戻る
using System;
using Lub;
using LiderCore;

public static class TestStages
{
    static int frames;
    static int stage;
    static float waited;
    static bool failed;
    static Window? window;

    public static void OnInit()
    {
        TestInit();
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
        TestFrame(dt);
        Lider.OnFrame(dt);
    }

    static void TestInit()
    {
        App.Config(new ConfigOpts { Width = 1280, Height = 720 });
    }

    static void TestFrame(float dt)
    {
        if (failed)
            return;
        frames++;
        var w = Lider.Work!;
        var path = Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!;
        if (frames == 2)
            w.FocusOn(path);
        if (frames < 6)
            return;
        waited += dt;
        // 窓の濃さが動き終わるのを待つ
        if (waited < 0.6f)
            return;
        waited = 0;
        window ??= w.Windows[0];
        switch (stage++)
        {
            case 0:
                Check(w.Active && !Lider.GameOnly && Lider.Opacity == 1, "not editing at first");
                Key(Keys.F11, 0);
                break;
            case 1:
                Check(window.PxW > PanelView.BasePxW, "F11 did not widen the window: " + window.PxW);
                Lider.Search.Open(w);
                Key(Keys.F1, 0);
                Check(!w.Active && !Lider.GameOnly, "F1 did not hand the input to the game");
                Check(!Lider.Search.Shown, "the search stayed open");
                w.BuildFailed(path + "(3,1): error CS0001: broken");
                break;
            case 2:
                Check(Lider.Opacity < 0.5f && Lider.Opacity > 0, "the windows are not faint: " + Lider.Opacity);
                Check(window.PxW == PanelView.BasePxW, "the Zen window stays wide: " + window.PxW);
                Check(Lider.Panel(window).Errors.Count == 1, "the window did not take the error");
                Key(Keys.F1, 0);
                Check(w.Active, "F1 did not return to editing");
                break;
            case 3:
                Check(Lider.Opacity == 1, "the windows did not come back: " + Lider.Opacity);
                Check(window.PxW > PanelView.BasePxW, "Zen did not come back: " + window.PxW);
                Key(Keys.F1, 0);
                Key(Keys.F1, Keys.ModShift);
                Check(!w.Active && Lider.GameOnly, "Shift+F1 did not show the game only");
                break;
            case 4:
                Key(Keys.F1, Keys.ModShift);
                Check(!w.Active && !Lider.GameOnly, "Shift+F1 did not return to trying");
                Key(Keys.F1, Keys.ModShift);
                Key(Keys.F1, 0);
                Check(w.Active && !Lider.GameOnly, "F1 did not return from the game only to editing");
                if (!failed)
                {
                    Console.WriteLine("PASS");
                    App.Quit(0);
                }
                break;
        }
    }

    static void Key(int key, int mods)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = mods, Typed = 0, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = 0, Typed = 0, Plain = 0 });
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
