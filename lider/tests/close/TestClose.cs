// focus をどのウィンドウにも移せることと、ウィンドウを閉じる操作がどのウィンドウにも同じに効くこと。
//   - F2 は、インスペクタに focus が無ければ focus を移し、あれば閉じる。閉じていれば出して focus を移す
//   - Ctrl+W は focus のあるウィンドウを閉じる (インスペクタなら focus は Code に戻り、Code ならタブを閉じる)
//   - インスペクタの本文を押すと focus が移り、見出しの閉じる印を押すと閉じる
using System;
using Lub;
using LiderCore;

public static class TestClose
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
        var inspector = Lider.InspectWindow;
        if (frames == 2)
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
        if (frames < 6)
            return;
        // 窓と視点が動き終わるのを待つ
        waited += dt;
        if (waited < 0.6f)
            return;
        waited = 0;
        var board = Lider.BoardViewport();
        switch (stage++)
        {
            case 0:
                Check(Lider.Inspector.Shown && w.Focused == w.FocusWindow && w.FocusWindow != null, "the focus is not on the code at first");
                Key(Keys.F2, 0);
                Check(Lider.Inspector.Shown && w.Focused == inspector, "F2 did not move the focus to the inspector");
                Key(Keys.F2, 0);
                Check(!Lider.Inspector.Shown && w.Focused == w.FocusWindow, "F2 on the focused inspector did not close it");
                Key(Keys.F2, 0);
                Check(Lider.Inspector.Shown && w.Focused == inspector, "F2 did not open the inspector with the focus");
                break;
            case 1:
                Key(26, Keys.ModCtrl);
                Check(!Lider.Inspector.Shown && w.Focused == w.FocusWindow, "Ctrl+W did not close the focused inspector");
                Check(w.Windows.Count == 1, "Ctrl+W on the inspector closed a code window");
                Key(Keys.F2, 0);
                Key(Keys.F1, 0);
                Key(Keys.F1, 0);
                Check(w.Focused == w.FocusWindow, "F1 did not bring the focus back to the code");
                break;
            case 2:
                Click((board.X + board.Z) * 0.5f, board.W - 8);
                Check(w.Focused == inspector, "a click on the inspector did not move the focus to it");
                Key(Keys.F1, 0);
                Key(Keys.F1, 0);
                // 見出しの帯は板のすぐ上にあり、右の端が閉じる印
                Click((board.X + board.Z) * 0.5f, board.Y - 8);
                Check(Lider.Inspector.Shown && w.Focused == inspector, "a click on the title did not move the focus");
                Click(board.Z - 8, board.Y - 8);
                Check(!Lider.Inspector.Shown && w.Focused == w.FocusWindow, "a click on the close mark did not close the inspector");
                break;
            case 3:
                Key(26, Keys.ModCtrl);
                Check(w.Windows.Count == 0, "Ctrl+W on the code did not close its last tab");
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
