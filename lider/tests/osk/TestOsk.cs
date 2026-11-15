// 画面のキーボード: 並んだ key を押すと、つないだキーボードと同じに効く。
//   - 命令 keyboard:toggle で出し入れする
//   - 文字の key を押すと、focus のある Code に字が入る
//   - Shift は次の 1 打にだけ効く
//   - Ctrl を押してから P で、検索が開く。Esc で閉じる
//   - F1 で focus がゲームに移り、その間もキーボードは押せて、もう一度 F1 で戻る
using System;
using Lub;
using LiderCore;

public static class TestOsk
{
    static int frames;
    static int stage;
    static float waited;
    static bool failed;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 1280, Height = 720 });
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
        if (failed)
            return;
        frames++;
        var w = Lider.Work!;
        if (frames == 2)
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
        if (frames < 6)
            return;
        waited += dt;
        if (waited < 0.4f)
            return;
        waited = 0;
        var osk = Lider.Keyboard;
        var window = Lider.KeyboardWindow;
        var doc = w.FocusWindow!.Code.Doc;
        switch (stage++)
        {
            case 0:
                Check(!osk.Shown && w.ScreenOf(window) == null, "the keyboard is out at first");
                Check(Lider.Run("keyboard:toggle") && osk.Shown, "keyboard:toggle did not show the keyboard");
                break;
            case 1:
                Check(w.ScreenOf(window) != null && window.Position != null && window.Size.X > 0, "the keyboard window is not placed");
                Press("h");
                Press("Shift");
                Check((osk.Mods & Keys.ModShift) != 0, "Shift is not held for the next key");
                // Shift を効かせている間、文字の key の字は大文字になる
                Press("I");
                Check(osk.Mods == 0, "Shift is still held after a key");
                Press("1");
                break;
            case 2:
                Check(doc.Text(0).StartsWith("hI1"), "typed '" + doc.Text(0) + "', want 'hI1'");
                Press("Ctrl");
                Press("p");
                Check(Lider.Search.Shown, "Ctrl then P did not open the search");
                Press("Esc");
                Check(!Lider.Search.Shown, "Esc did not close the search");
                Press("F1");
                Check(w.OnGame, "F1 did not move the focus to the game");
                break;
            case 3:
                Press("F1");
                Check(!w.OnGame, "F1 on the keyboard did not bring the focus back from the game");
                Check(Lider.Run("keyboard:toggle") && !osk.Shown, "keyboard:toggle did not hide the keyboard");
                break;
            case 4:
                Check(w.ScreenOf(window) == null && window.Position == null, "the keyboard window is still on a screen");
                if (!failed)
                {
                    Console.WriteLine("PASS");
                    App.Quit(0);
                }
                break;
        }
    }

    // 字が label の key の真ん中を、マウスの左ボタンで押す
    static void Press(string label)
    {
        var osk = Lider.Keyboard;
        var window = Lider.KeyboardWindow;
        var screen = Lider.Work!.ScreenOf(window);
        if (screen == null || window.Position == null)
        {
            Check(false, "the keyboard is not out");
            return;
        }
        for (var r = 0; r < osk.Rows.Count; r++)
        {
            var left = 0.0f;
            foreach (var key in osk.Rows[r])
            {
                if (osk.Label(key) == label)
                {
                    var u = (left + key.W * 0.5f) / Osk.Units;
                    var v = (r + 0.5f) / osk.Rows.Count;
                    var at = Lider.SurfacePx(screen, window.Position.Value.X + (u - 0.5f) * window.Size.X, window.Position.Value.Y + (0.5f - v) * window.Size.Y);
                    Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = 1, X = at.X, Y = at.Y });
                    Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonUp, Button = 1, X = at.X, Y = at.Y });
                    return;
                }
                left += key.W;
            }
        }
        Check(false, "no key " + label);
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
