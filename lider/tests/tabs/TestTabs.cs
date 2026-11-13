// 窓のタブ。
//   - file を開くと focus のある窓のタブに足し、窓は増えない。開いている file はそのタブを出す
//   - Ctrl+Tab / Ctrl+Shift+Tab で隣のタブへ (端から反対の端へ回る)
//   - Ctrl+Alt+→ / ← で今のタブを隣の窓へ移す。端なら新しい窓を作る。空になった窓は閉じる
//   - Ctrl+W と Escape は今のタブを閉じ、最後のタブを閉じると窓も閉じる
//   - 見出しにタブの名前を並べ、押した所のタブを出す
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestTabs
{
    const int Tab = 43;
    const int W = 26;
    static int frames;
    static bool failed;
    static Window? first;

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
        Test(dt);
        Lider.OnFrame(dt);
    }

    static void Test(float dt)
    {
        frames++;
        if (failed)
            return;
        var w = Lider.Work!;
        var target = Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!;
        var second = target.Replace("Target.cs", "Second.cs");
        var third = target.Replace("Target.cs", "Third.cs");
        if (frames == 1)
        {
            w.FocusOn(target);
            first = w.FocusWindow!;
            w.FocusOn(second);
            Check(w.Windows.Count == 1 && first.Tabs.Count == 2 && w.Focus!.Path == second, "opening a second file did not add a tab: " + Shape(w));
            w.FocusOn(target);
            Check(w.Windows.Count == 1 && first.Tab == 0 && w.Focus!.Path == target, "opening an open file did not show its tab: " + Shape(w));
            w.FocusOn(third);
            Check(Shape(w) == "[Target.cs *Third.cs Second.cs]", "a new tab goes right of the current tab: " + Shape(w));

            Press(Tab, Keys.ModCtrl);
            Press(Tab, Keys.ModCtrl);
            Check(Shape(w) == "[*Target.cs Third.cs Second.cs]", "Ctrl+Tab did not wrap to the first tab: " + Shape(w));
            Press(Tab, Keys.ModCtrl | Keys.ModShift);
            Check(Shape(w) == "[Target.cs Third.cs *Second.cs]", "Ctrl+Shift+Tab did not wrap to the last tab: " + Shape(w));

            Press(Keys.Right, Keys.ModCtrl | Keys.ModAlt);
            Check(Shape(w) == "[Target.cs *Third.cs] [*Second.cs]", "Ctrl+Alt+Right did not move the tab to a new window: " + Shape(w));
            Check(w.FocusWindow == w.Windows[1], "the focus did not follow the moved tab");
            Press(Keys.Left, Keys.ModCtrl | Keys.ModAlt);
            Check(Shape(w) == "[Target.cs Third.cs *Second.cs]", "Ctrl+Alt+Left did not move the tab back and close the empty window: " + Shape(w));

            Press(W, Keys.ModCtrl);
            Check(Shape(w) == "[Target.cs *Third.cs]", "Ctrl+W did not close the tab: " + Shape(w));
            Press(Keys.Escape, 0);
            Check(Shape(w) == "[*Target.cs]", "Escape did not close the tab: " + Shape(w));
            w.FocusOn(second);
            return;
        }
        if (frames == 3)
        {
            // 見出しの 2 つ目のタブを押す (1 frame 描いた後の位置)
            var panel = Lider.Panel(first!);
            var labels = panel.TabLabels;
            Check(labels.Count == 2 && labels[0] == "Target.cs" && labels[1] == "Second.cs", "the title shows " + string.Join("|", labels));
            if (failed)
                return;
            var at = panel.TabAt(panel.TabX(0) / panel.PxW);
            Check(at == 0, "the title at the first tab is tab " + at);
            Lider.ShowTab(first!, at);
            Check(Shape(w) == "[*Target.cs Second.cs]", "pressing the first tab did not show it: " + Shape(w));
            Press(W, Keys.ModCtrl);
            Press(W, Keys.ModCtrl);
            Check(w.Windows.Count == 0 && w.FocusWindow == null, "closing the last tab did not close the window: " + Shape(w));
            if (failed)
                return;
            Console.WriteLine("PASS");
            App.Quit(0);
        }
    }

    // 窓ごとのタブの名前 (今のタブに *)
    static string Shape(Workspace w)
    {
        var s = "";
        foreach (var window in w.Windows)
        {
            var tabs = new List<string>();
            for (var i = 0; i < window.Tabs.Count; i++)
            {
                var p = window.Tabs[i].Path;
                tabs.Add((i == window.Tab ? "*" : "") + Utf8.Sub(p, Utf8.LastIndexOf(p, "/") + 1));
            }
            s += (s == "" ? "" : " ") + "[" + string.Join(" ", tabs) + "]";
        }
        return s;
    }

    static void Press(int key, int mods)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = mods });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = mods });
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
