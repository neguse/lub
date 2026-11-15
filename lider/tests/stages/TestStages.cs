// focus をゲームの絵と Code の間で移すことと、ゲームの絵でないスクリーンを隠すこと。
//   - F1 で focus がゲームに移る: 入力はゲームに渡り、窓は同じ所に薄く残り、compile error のような窓の中身は描き続ける。もう一度 F1 で Code に戻り、濃く戻る
//   - focus がゲームにある間は Zen の広げた窓を元の大きさにし、Code に戻ると広げ直す。検索は閉じる
//   - 命令 screen:hide でゲームの絵でないスクリーンを隠し、もう一度で出す。隠している間に F1 を押すと、出して focus を Code に移す
//   - ゲームの絵を押すと focus がゲームに移り、窓を押すと Code に戻る
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
                Check(!w.OnGame && !Lider.Hidden && Lider.Opacity == 1, "not editing at first");
                Key(Keys.Return, Keys.ModAlt);
                break;
            case 1:
                Check(window.PxW > PanelView.BasePxW, "Alt+Enter did not widen the window: " + window.PxW);
                Lider.Search.Open(w);
                Key(Keys.F1, 0);
                Check(w.OnGame && !Lider.Hidden, "F1 did not hand the input to the game");
                Check(!Lider.Search.Shown, "the search stayed open");
                w.BuildFailed(path + "(3,1): error CS0001: broken");
                break;
            case 2:
                Check(Lider.Opacity < 0.5f && Lider.Opacity > 0, "the windows are not faint: " + Lider.Opacity);
                Check(window.PxW == PanelView.BasePxW, "the Zen window stays wide: " + window.PxW);
                Check(Lider.Panel(window).Errors.Count == 1, "the window did not take the error");
                Key(Keys.F1, 0);
                Check(!w.OnGame, "F1 did not return to editing");
                break;
            case 3:
                Check(Lider.Opacity == 1, "the windows did not come back: " + Lider.Opacity);
                Check(window.PxW > PanelView.BasePxW, "Zen did not come back: " + window.PxW);
                Key(Keys.F1, 0);
                Lider.Run("screen:hide");
                Check(w.OnGame && Lider.Hidden, "screen:hide did not show the game only");
                break;
            case 4:
                Lider.Run("screen:hide");
                Check(w.OnGame && !Lider.Hidden, "screen:hide did not show the screens again");
                Lider.Run("screen:hide");
                Key(Keys.F1, 0);
                Check(!w.OnGame && !Lider.Hidden, "F1 did not return from the game only to editing");
                break;
            case 5:
                // ゲームの絵 (窓の無い所) を押すと focus がゲームに移り、窓を押すと Code に戻る
                Click(2, 2);
                Check(w.OnGame && w.Focused != window, "a click on the game did not move the focus to the game");
                Check(w.FocusWindow == window, "the code window was forgotten");
                break;
            case 6:
                Click(Lider.CaretViewport.X + 4, Lider.CaretViewport.Y + 4);
                Check(!w.OnGame && w.Focused == window, "a click on the window did not move the focus to the code");
                if (!failed)
                {
                    Console.WriteLine("PASS");
                    App.Quit(0);
                }
                break;
        }
    }

    static void Click(float x, float y)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = 1, X = x, Y = y });
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonUp, Button = 1, X = x, Y = y });
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
