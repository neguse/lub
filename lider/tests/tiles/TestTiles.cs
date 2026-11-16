// 区画に割る規則 (MainStack) の設定を、利用者の操作で変える。
//   - 起動したときの左右に並ぶ形は、先頭の 1 枚 (ゲームの絵) が左の列、残りが右の列に積まれる
//   - 命令で、先頭に 1 枚ずつ並べるウィンドウを増やすと、列が増える (ゲームの絵 | Code | Inspector)。減らすと戻る
//   - 列の境目 (ウィンドウの右の端) をマウスで引くと、列の幅の割合が変わる
//   - 積んだウィンドウの境目 (下の端) を引くと、高さの割合が変わる
//   - 命令で、focus のあるウィンドウの幅を変えられる (右の端の列は、左の境目が動く)
//   - 変えた設定は、規則の書き方に出て、読み直すと同じ並びになる
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestTiles
{
    static int frames;
    static int stage;
    static bool failed;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 1200, Height = 600 });
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
        TestFrame();
        Lider.OnFrame(dt);
    }

    static void TestFrame()
    {
        if (failed)
            return;
        frames++;
        var w = Lider.Work!;
        var backdrop = Lider.Screens[0];
        if (frames == 2)
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
        // 1 つの操作ごとに、並べ直して画面の上の矩形が決まるまで待つ
        if (frames < 10 || frames % 5 != 0)
            return;
        var game = backdrop.Windows[0];
        var code = w.FocusWindow!;
        var inspector = Lider.InspectWindow;
        var g = game.ViewportRect;
        var c = code.ViewportRect;
        var i = inspector.ViewportRect;
        Check(g != null && c != null && i != null, "a window on the backdrop has no rectangle at stage " + stage);
        if (failed)
            return;
        switch (stage++)
        {
            case 0:
                Check(Near(g!.W, 600) && Near(c!.X, 600) && Near(c.W, 600) && Near(c.H, 300) && Near(i!.X, 600) && Near(i.Y, 300),
                    "at first: game " + Show(g) + " code " + Show(c!) + " inspector " + Show(i!));
                Lider.Run("layout:more-mains");
                break;
            case 1:
                Check(Near(g!.W, 400) && Near(c!.X, 400) && Near(c.W, 400) && Near(c.H, 600) && Near(i!.X, 800) && Near(i.H, 600),
                    "with two mains: game " + Show(g) + " code " + Show(c!) + " inspector " + Show(i!));
                Lider.Run("layout:fewer-mains");
                break;
            case 2:
                Check(Near(g!.W, 600) && Near(c!.H, 300), "back to one main: game " + Show(g) + " code " + Show(c!));
                // ゲームの絵の右の端のすぐ外を押して、左へ引く
                Mouse(EventKind.MouseButtonDown, g.X + g.W + 3, 150);
                Mouse(EventKind.MouseMotion, 450, 150);
                Mouse(EventKind.MouseButtonUp, 450, 150);
                Check(w.Focused == code, "dragging an edge moved the focus");
                break;
            case 3:
                Check(Near(g!.W, 450) && Near(c!.X, 450) && Near(c.W, 750) && Near(i!.W, 750),
                    "after dragging the column edge: game " + Show(g) + " code " + Show(c!) + " inspector " + Show(i!));
                // Code の下の端のすぐ外を押して、下へ引く
                Mouse(EventKind.MouseButtonDown, 800, c.Y + c.H + 3);
                Mouse(EventKind.MouseMotion, 800, 420);
                Mouse(EventKind.MouseButtonUp, 800, 420);
                break;
            case 4:
                Check(Near(c!.H, 420) && Near(i!.Y, 420) && Near(i.H, 180),
                    "after dragging the stacked edge: code " + Show(c) + " inspector " + Show(i!));
                var text = Layouts.Describe(backdrop.Layout);
                Check(text.Contains("mains=1") && text.Contains("widths=") && text.Contains("heights="), "the layout is written as " + text);
                var before = g!.W;
                backdrop.Layout = Layouts.Parse(text);
                backdrop.Arrange();
                Check(Near(game.Size.X / backdrop.Size.X * 1200, before), "the layout read back differs: " + Layouts.Describe(backdrop.Layout));
                // focus は Code (右の端の列) にある。幅を広げると、左の境目が動く
                Lider.Run("window:widen");
                break;
            case 5:
                Check(g!.W < 445 && Near(c!.X, g.W) && Near(c.X + c.W, 1200), "after window:widen: game " + Show(g) + " code " + Show(c!));
                Console.WriteLine("PASS");
                App.Quit(0);
                break;
        }
    }

    static bool Near(float a, float b)
    {
        return Math.Abs(a - b) <= 2;
    }

    static string Show(PixelRect r)
    {
        return r.X + "," + r.Y + " " + r.W + "x" + r.H;
    }

    static void Mouse(EventKind kind, float x, float y)
    {
        Lider.OnEvent(new EventData { Kind = kind, Button = 1, X = x, Y = y });
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
