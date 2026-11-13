// 円筒に並べる窓と、見ている窓への focus。
//   - タブを右の端の窓から右へ移すと窓が 2 つ並び、2 つ目は 1 つ目の右隣りに置く
//   - 窓は大きさが変わっても重ならない
//   - Alt + 左右と Alt + 数字で focus が移り、Alt + Shift + 左右で並びが変わる
//   - つかんだ窓が隣の中心を越えると並びが入れ替わり、離すと列に収まる
//   - 閉じると隙間が詰まり、右隣に focus が移る。閉じた窓をもう一度閉じても何も起きない
//   - lider を隠している間に離した Alt は、出し直した後に押したままにならない
//   - 平面の視点は focus のある窓が入る所まで動く (Space.FollowX)。XR の向きの印は視野の外だけ (Space.OffView)
//   - 見ている窓に focus が移るのは、Workspace.LookSeconds 見続けたとき
//   - 打ってから Workspace.QuietSeconds の間は、見続けても移らない
//   - タブ 1 つの窓で Escape を押すと窓が閉じ、残りの窓に focus が移る
//   - 窓の見出しに、Root からの path と未保存の印が出る
//   - 見出しを押すと focus が移り、閉じる印を押すと閉じる (focus の無い窓でも)。閉じても未保存の本文は残る
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestWindows
{
    static bool ran;
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
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    // 時間を進めながら、ずっと window を見る
    static void LookFor(Workspace w, Window window, float seconds)
    {
        var dt = 0.05f;
        for (var t = 0.0f; t < seconds - 0.001f; t += dt)
        {
            w.Tick(dt);
            w.Look(window, dt);
        }
    }

    static void TestFrame(float dt)
    {
        if (ran)
            return;
        ran = true;
        var target = Environment.GetEnvironmentVariable("LIDER_TEST_FILE");
        var w = Lider.Work;
        Check(target != null && w != null, "LIDER_TEST_FILE or the workspace is missing");
        if (failed)
            return;
        var second = target!.Replace("Target.cs", "Second.cs");
        w!.FocusOn(target);
        var first = w.FocusWindow!;
        w.FocusOn(second);
        w.MoveTab(1);
        var next = w.FocusWindow!;
        Check(w.Windows.Count == 2 && first != next, "moving a tab right of the last window did not make two windows");
        var a = Lider.WindowAngle(w, first);
        var b = Lider.WindowAngle(w, next);
        Check(b > a + PanelView.BaseW / Space.R, "the second window is not right of the first: " + a + ", " + b);
        w.FocusOn(target);
        Check(w.Windows.Count == 2 && w.FocusWindow == first, "opening an open file made another window");
        w.FocusOn(second);

        LookFor(w, first, Workspace.LookSeconds * 0.5f);
        Check(w.FocusWindow == next, "focus moved before looking long enough");
        LookFor(w, first, Workspace.LookSeconds);
        Check(w.FocusWindow == first, "focus did not move to the window looked at");

        w.Key(Keys.A, true);
        w.Key(Keys.A, false);
        LookFor(w, next, Workspace.QuietSeconds * 0.8f);
        Check(w.FocusWindow == first, "focus moved while typing");
        LookFor(w, next, Workspace.QuietSeconds);
        Check(w.FocusWindow == next, "focus did not move after the typing stopped");

        w.Key(Keys.Escape, true);
        Check(w.Windows.Count == 1 && w.FocusWindow == first, "Escape did not close the focused window");

        Strip(w, first, target, second);
        first = w.FocusWindow!;

        w.FocusOn(second);
        w.MoveTab(1);
        next = w.FocusWindow!;
        Check(w.Title(next) == "Second.cs", "the title is not the path under the root: " + w.Title(next));
        Press(w, next, 0.5f);
        Check(w.FocusWindow == next, "pressing the title changed the focus to the wrong window");
        Press(w, first, 0.5f);
        Check(w.FocusWindow == first, "pressing the title did not move the focus");
        EditorHost.Insert(next.Code.ViewId, "// x\n");
        Check(w.Title(next) == "Second.cs *", "the title does not mark the unsaved text: " + w.Title(next));
        Press(w, next, 0.99f);
        Check(w.Windows.Count == 1 && w.FocusWindow == first, "the close mark did not close a window without focus");
        w.FocusOn(second);
        Check(w.FocusWindow!.Code.Doc.Text(0) == "// x", "closing lost the unsaved text");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void Strip(Workspace w, Window first, string target, string second)
    {
        var third = target.Replace("Target.cs", "Third.cs");
        w.FocusWindow = first;
        w.FocusOn(second);
        w.MoveTab(1);
        var b = w.FocusWindow!;
        w.FocusOn(third);
        w.MoveTab(1);
        var c = w.FocusWindow!;
        w.SetMods(Keys.ModShift);
        Lider.WindowKey(w, Keys.Left);
        w.SetMods(0);
        Check(Order(w) == "Target Third Second", "the windows are not in order: " + Order(w));
        c.Scale = 2;
        w.Arrange(null);
        var a0 = Lider.WindowAngle(w, first);
        var a1 = Lider.WindowAngle(w, c);
        var a2 = Lider.WindowAngle(w, b);
        Check(a0 == 0, "the first window is not at the origin");
        Near((a1 - a0) * Space.R, PanelView.BaseW * (first.Scale + c.Scale) * 0.5f + LiderCore.Strip.Gap, "the gap after the first window");
        Near((a2 - a1) * Space.R, PanelView.BaseW * (c.Scale + b.Scale) * 0.5f + LiderCore.Strip.Gap, "the gap after the scaled window");
        c.Scale = 1;

        w.FocusWindow = first;
        Lider.WindowKey(w, Keys.Left);
        Check(w.FocusWindow == first, "Alt+Left at the left end moved the focus");
        Lider.WindowKey(w, Keys.Right);
        Check(w.FocusWindow == c, "Alt+Right did not move to the neighbour");
        Lider.WindowKey(w, Keys.Digit1 + 2);
        Check(w.FocusWindow == b, "Alt+3 did not move to the third window");
        w.SetMods(Keys.ModShift);
        Lider.WindowKey(w, Keys.Left);
        w.SetMods(0);
        Check(Order(w) == "Target Second Third" && w.FocusWindow == b, "Alt+Shift+Left did not reorder: " + Order(w));

        w.Arrange(null);
        var grab = new Grab();
        grab.Start(first, first.Bounds!.X, 0);
        grab.Move(b.Bounds!.X + 0.01f, 0.2f);
        w.Arrange(first);
        Check(Order(w) == "Second Target Third", "dragging over the neighbour's center did not reorder: " + Order(w));
        Check(first.Bounds!.Y == 0.2f, "the held window did not stay at the pointer");
        grab.Release();
        w.Arrange(null);
        Check(first.Bounds!.Y == 0 && b.Bounds!.X == 0, "the window did not settle in the row");

        w.FocusWindow = first;
        var keep = c.Bounds!.X;
        w.Close(first);
        w.Arrange(null);
        Check(w.FocusWindow == c && c.Bounds!.X < keep, "closing did not close the gap or focus the right neighbour");
        w.Close(c);
        Check(w.FocusWindow == b, "closing the right end did not focus the left neighbour");
        w.Close(b);
        w.Close(b);
        Check(w.Windows.Count == 0 || w.FocusWindow != null, "closing a closed window broke the focus");

        w.SetMods(Keys.ModAlt);
        Check(w.Alt, "Alt is not tracked");
        w.Active = false;
        w.SetMods(0);
        w.Active = true;
        Check(!w.Alt, "Alt stayed down after it was released while hidden");

        Near(Space.FollowX(0, 0.5f, 0.3f, 1), 0, "a visible window moved the view");
        Near(Space.FollowX(0, 2, 0.3f, 1), 1.3f, "the view did not go right to the window");
        Near(Space.FollowX(2, 0, 0.3f, 1), 0.7f + 0.0f, "the view did not go left to the window");
        Near(Space.FollowX(0, 2, 1.5f, 1), 2, "a window wider than the viewport is not centered");
        Check(Space.OffView(0, 0.3f, 0.1f, 0.5f) == 0, "a visible window has a cue");
        Check(Space.OffView(0, 1.5f, 0.1f, 0.5f) == 1 && Space.OffView(0, -1.5f, 0.1f, 0.5f) == -1, "the cue points the wrong way");
        Check(Space.OffView(3.0f, -3.0f, 0.1f, 0.5f) == 0, "the angle does not wrap around");

        w.FocusOn(target);
    }

    static string Order(Workspace w)
    {
        var s = "";
        foreach (var window in w.Windows)
        {
            var name = window.Code.Doc.Path;
            name = Utf8.Sub(name, Utf8.Bytes(name) - 9);
            s += (s == "" ? "" : " ") + name.Replace(".cs", "").Replace("/", "");
        }
        return s;
    }

    static void Near(float got, float want, string what)
    {
        Check(Math.Abs(got - want) < 0.001f, what + ": " + got + ", want " + want);
    }

    // window の見出しの帯の横 u の所を、見る人の位置から押す
    static void Press(Workspace w, Window window, float u)
    {
        var a = Lider.WindowAngle(w, window) + (u - 0.5f) * PanelView.BaseW * window.Scale / Space.R;
        var at = Space.At(a, Lider.Panel(window).TitleY(Lider.WindowY(w, window), window.Scale), Space.R);
        Lider.Click(w, new Ray(new Vec3(0, 0, 0), at), false);
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
