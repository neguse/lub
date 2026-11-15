// Alt+L (screen:next-layout) で、コードの窓をゲームの絵に重ねる形と、左右に並ぶ形を切り替える。
//   - 左右に並ぶ形では、コードの窓は背景のスクリーン (視点に固定した平面) に移り、画面の右半分に出る
//   - その窓は、右半分の真ん中を続けて 2 回押すと語を選べる (描いた所と、当たる所が合っている)
//   - 画面の左半分を押しても、コードの窓には当たらない
//   - 窓を右ボタンでつかんでゲームの絵の区画へ引くと、並びが入れ替わる
//   - もう一度 Alt+L で、コードのスクリーンに戻る
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestTiled
{
    const string Word = "abcdefghijklmnopqrstuvwxyz";
    static int stage;
    static float next;
    static bool failed;
    static int id;
    // 窓の開く動きの残り。動きは dt で進むので、重い frame があると実時間で待っても終わらない
    static float opening;

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
        TestFrame(dt);
        Lider.OnFrame(dt);
    }

    static void TestFrame(float dt)
    {
        opening -= dt;
        if (failed || opening > 0 || Sys.Seconds() < next)
            return;
        next = Sys.Seconds() + 0.5f;
        Gfx.Size(out var sw, out var sh);
        var w = Lider.Work!;
        if (stage == 0)
        {
            var path = Environment.GetEnvironmentVariable("LIDER_TEST_FILE");
            Check(path != null, "LIDER_TEST_FILE is missing");
            if (failed)
                return;
            Lider.Inspector.Shown = false;
            w.FocusOn(path!);
            id = w.FocusWindow!.Code.ViewId;
            opening = 1.5f;
        }
        else if (stage == 1)
        {
            Check(w.ScreenOf(w.FocusWindow!) == w.CodeScreen, "the window did not start on the code screen");
            NextLayout();
            Check(Lider.Tiled && w.ScreenOf(w.FocusWindow!) == Lider.Screens[0], "Alt+L did not move the window to the backdrop");
            opening = 1.5f;
        }
        else if (stage == 2)
        {
            var at = w.FocusWindow!.ViewportRect;
            Check(at != null && Math.Abs(at.X - sw * 0.5f) <= 1 && Math.Abs(at.W - sw * 0.5f) <= 1 && at.Y == 0 && Math.Abs(at.H - sh) <= 1,
                "the window is not the right half: " + (at != null ? at.X + "," + at.Y + " " + at.W + "x" + at.H : "none"));
            var game = Lider.Screens[0].Windows[0].ViewportRect;
            Check(game != null && game.X == 0 && Math.Abs(game.W - sw * 0.5f) <= 1, "the game is not the left half");
            // 左半分を押しても、コードの窓には当たらない
            EditorHost.Focus(id);
            w.FocusWindow.Code.Doc.MoveTo(0, 0);
            var before = EditorHost.CursorLine(id);
            Click(sw * 0.25f, sh * 0.5f);
            Check(EditorHost.CursorLine(id) == before, "a click on the game moved the cursor to line " + EditorHost.CursorLine(id));
            // 右半分の真ん中を 2 回押すと、そこの語を選ぶ
            Click(sw * 0.75f, sh * 0.5f);
            Click(sw * 0.75f, sh * 0.5f);
            Check(EditorHost.Selection(id) == Word, "a double click on the tiled window selected '" + EditorHost.Selection(id) + "'");
        }
        else if (stage == 3)
        {
            // 窓を右ボタンでつかんで左半分 (ゲームの絵の区画) へ引くと、並びが入れ替わる
            Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = 3, X = sw * 0.75f, Y = sh * 0.5f });
            Lider.OnEvent(new EventData { Kind = EventKind.MouseMotion, X = sw * 0.25f, Y = sh * 0.5f });
        }
        else if (stage == 4)
        {
            Check(Lider.Screens[0].Windows[0] == w.FocusWindow, "dragging the window over the game did not swap the order");
            Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonUp, Button = 3, X = sw * 0.25f, Y = sh * 0.5f });
        }
        else if (stage == 5)
        {
            var at = w.FocusWindow!.ViewportRect;
            Check(at != null && at.X == 0 && Math.Abs(at.W - sw * 0.5f) <= 1, "the dropped window is not the left half");
            var game = Lider.Screens[0].Windows[1].ViewportRect;
            Check(Lider.Screens[0].Windows[1].Content is GameView && game != null && Math.Abs(game.X - sw * 0.5f) <= 1, "the game is not the right half");
            // 元に戻す
            Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = 3, X = sw * 0.25f, Y = sh * 0.5f });
            Lider.OnEvent(new EventData { Kind = EventKind.MouseMotion, X = sw * 0.75f, Y = sh * 0.5f });
        }
        else if (stage == 6)
        {
            Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonUp, Button = 3, X = sw * 0.75f, Y = sh * 0.5f });
            Check(Lider.Screens[0].Windows[0].Content is GameView, "dragging the window back did not restore the order");
        }
        else if (stage == 7)
        {
            NextLayout();
            Check(!Lider.Tiled && w.ScreenOf(w.FocusWindow!) == w.CodeScreen, "Alt+L did not move the window back");
            Check(Lider.Screens[0].Windows.Count == 1, "the backdrop kept " + Lider.Screens[0].Windows.Count + " windows");
            opening = 1.5f;
        }
        else if (stage == 8)
        {
            // 重ねる形に戻ると、窓は画面の真ん中にある
            Click(sw * 0.5f, sh * 0.5f);
            Click(sw * 0.5f, sh * 0.5f);
            Check(EditorHost.Selection(id) == Word, "a double click after going back selected '" + EditorHost.Selection(id) + "'");
        }
        else
        {
            Console.WriteLine("PASS");
            App.Quit(0);
            return;
        }
        if (!failed && EditorHost.LastError() != "")
            Check(false, "editor error: " + EditorHost.LastError());
        stage++;
    }

    // Alt+L (並べ方の規則を替える)
    static void NextLayout()
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = 15, Mods = Keys.ModAlt });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = 15, Mods = 0 });
    }

    static void Click(float x, float y)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = 1, X = x, Y = y });
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonUp, Button = 1, X = x, Y = y });
    }

    static void Check(bool cond, string message)
    {
        if (cond || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL: " + message);
        App.Quit(1);
    }
}
