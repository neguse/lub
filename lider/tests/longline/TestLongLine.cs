// 窓より長い行と、窓の横の scroll。
//   - cursor を窓より長い行の末尾へ動かすと、窓は横に scroll し、cursor は窓の中に見える
//   - scroll しなくても cursor が見える行へ動かすと、窓は左の端に戻る
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestLongLine
{
    // Target.cs の窓より長い行と、その下の短い行 (0 から)
    const int LongLine = 3;
    const int ShortLine = 4;
    static int frames;
    static bool failed;
    static float waited;
    static int stage;
    static float left;

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
        frames++;
        waited += dt;
        if (failed)
            return;
        var w = Lider.Work!;
        if (frames == 2)
        {
            waited = 0;
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
            w.Focus!.MoveTo(LongLine, 0);
            return;
        }
        // 窓が開いて止まるまで (2 秒) 待つ
        if (frames < 3 || waited < 1.5f)
            return;
        var doc = w.Focus!;
        var view = w.FocusWindow!.ViewId;
        if (stage == 0)
        {
            Check(InWindow(w), "the caret at the start of the long line is outside the window");
            left = EditorHost.TextX(view);
            doc.MoveTo(LongLine, Utf8.Bytes(doc.Text(LongLine)));
        }
        else if (stage == 1)
        {
            Check(EditorHost.TextX(view) < left, "the window did not scroll to the end of the long line: text at " + EditorHost.TextX(view) + ", left " + left);
            Check(InWindow(w), "the caret at the end of the long line is outside the window");
            doc.MoveTo(ShortLine, 4);
        }
        else
        {
            Check(EditorHost.TextX(view) == left, "the window stayed scrolled on a short line: text at " + EditorHost.TextX(view) + ", want " + left);
            Check(InWindow(w), "the caret on the short line is outside the window");
            if (failed)
                return;
            Console.WriteLine("PASS");
            App.Quit(0);
            return;
        }
        stage++;
        waited = 1;
    }

    // focus のある窓の cursor (Lider.CaretViewport) が、窓の左右の端の間にあるか
    static bool InWindow(Workspace w)
    {
        var window = w.FocusWindow!;
        var angle = Lider.WindowAngle(w, window);
        var half = window.W * window.Scale * 0.5f;
        var l = Lider.ViewportPoint(Space.At(angle - half / Space.R, window.Y, Space.R));
        var r = Lider.ViewportPoint(Space.At(angle + half / Space.R, window.Y, Space.R));
        var x = Lider.CaretViewport.X;
        if (x > l.X && x < r.X)
            return true;
        Console.WriteLine("caret x " + x + ", window " + l.X + " - " + r.X);
        return false;
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
