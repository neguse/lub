// 窓のリサイズと Zen。
//   - Alt+Shift+PageUp と窓の右端を引く操作で、その窓だけ幅が変わる。文字の密度は変わらないので長い行が先まで見える
//   - 幅と高さには下限がある
//   - F11 で focus のある窓が画面いっぱいになり、もう一度で利用者の大きさに戻る。隣の窓は変わらない
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestZen
{
    static int frames;
    static bool failed;

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
        if (frames == 4)
        {
            // 隣の窓を作る (開くと今の窓のタブに入るので、右の窓へ移す)
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_ROOT") + "/TestZen.cs");
            w.MoveTab(1);
        }
        // lider が 2 つの窓を置いた後に、窓が広がり終わるまで 1 秒分進める (開いた frame の重さで進む分に頼らない)
        if (frames == 6)
            for (var i = 0; i < 60; i++)
                Lider.Animate(w, 1.0f / 60);
        if (frames < 8)
            return;
        var other = w.Windows[1];
        var window = w.Windows[0];
        if (frames == 8)
        {
            w.FocusWindow = window;
            Check(window.PxW == PanelView.BasePxW, "the width at first: " + window.PxW);
            Alt(Keys.PageUp);
            Check(window.WidthPx == PanelView.BasePxW + 64, "Alt+Shift+PageUp: " + window.WidthPx);
            Check(other.WidthPx == PanelView.BasePxW, "the neighbor changed: " + other.WidthPx);
        }
        if (frames == 10)
        {
            Check(window.PxW == PanelView.BasePxW + 64 && Lider.Panel(window).PxW == window.PxW, "the panel width: " + Lider.Panel(window).PxW);
            Check(Longest(window.Code.ViewId, window.PxW) > PanelView.BasePxW, "the long line is cut at the old width");
            // 右端を引く
            var scale = window.Scale;
            var angle = Lider.WindowAngle(w, window);
            var right = angle + (window.W * scale * 0.5f + 0.005f) / Space.R;
            var ray = new Ray(new Vec3(0, 0, 0), Space.At(right, Lider.WindowY(w, window), Space.R));
            var edge = Lider.EdgeAt(w, window, ray);
            Check((edge & 1) != 0, "the right edge is not found: " + edge);
            var before = window.WidthPx;
            Lider.StartEdge(window, 1);
            Lider.DragEdge(w, new Ray(new Vec3(0, 0, 0), Space.At(right + 0.1f / Space.R, Lider.WindowY(w, window), Space.R)));
            Lider.EndEdge();
            Check(window.WidthPx > before, "dragging did not widen: " + before + " -> " + window.WidthPx);
            window.Resize(1, 1);
            Check(window.WidthPx == Window.MinPxW && window.HeightPx == Window.MinPxH, "no lower limit: " + window.WidthPx);
            window.Resize(900, PanelView.BasePxH);
        }
        if (frames == 12)
            Press();
        if (frames == 16)
        {
            Check(window.PxW > 900, "F11 did not widen the window: " + window.PxW);
            Check(window.WidthPx == 900, "F11 changed the chosen width: " + window.WidthPx);
            Check(other.PxW == PanelView.BasePxW, "F11 widened the neighbor: " + other.PxW);
            Press();
        }
        if (frames == 20)
        {
            Check(window.PxW == 900, "F11 did not restore: " + window.PxW);
            if (!failed)
            {
                Console.WriteLine("PASS");
                App.Quit(0);
            }
        }
    }

    static void Alt(int key)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = Keys.ModAlt | Keys.ModShift, Typed = 0, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = 0, Typed = 0, Plain = 0 });
    }

    // 最後の行まで見えるようにして、描く命令の右端を測る
    static float Longest(int id, int pxW)
    {
        EditorHost.ScrollToLine(id, 90);
        var n = EditorHost.Draw(id, pxW, PanelView.BasePxH);
        var right = 0f;
        for (var i = 0; i < n; i++)
            if (EditorHost.CmdKind(i) == 2)
                right = Math.Max(right, EditorHost.CmdX(i) + EditorHost.CmdW(i));
        return right;
    }

    static void Press()
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = Keys.F11, Mods = 0, Typed = 0, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = Keys.F11, Mods = 0, Typed = 0, Plain = 0 });
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
