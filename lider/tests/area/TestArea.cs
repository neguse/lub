// 非 XR で、インスペクタと focus のある窓を置く画面の範囲 (Lider.SetArea、LIDER_AREA)。
//   - 窓とインスペクタは範囲の中に収まり、窓の右の端は範囲の右の端に、インスペクタは窓の左に並ぶ
//   - 縦も範囲に収まる
//   - 窓が無い間も、インスペクタは範囲の左の端にある
//   - 止めると、控えた frame の帯と止めた位置の印が範囲の下の端に出る
using System;
using Lub;
using LiderCore;

public static class TestArea
{
    const float X = 320;
    const float Y = 0;
    const float W = 800;
    const float H = 540;
    static int frames;
    static float waited;
    static bool failed;
    static bool pausing;

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
        if (pausing)
        {
            Pause();
            return;
        }
        var w = Lider.Work!;
        if (frames == 2)
        {
            Lider.SetArea(X, Y, W, H);
            return;
        }
        if (frames == 4)
        {
            var alone = Lider.BoardViewport();
            Check(Near(alone.X, X), "without a window, the inspector's left edge is at " + alone.X + ", want " + X);
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
            waited = 0;
            return;
        }
        // 窓が開いて止まるまで (2 秒) 待つ
        if (frames < 5 || waited < 2)
            return;
        var window = w.FocusWindow!;
        var angle = Lider.WindowAngle(w, window);
        var half = window.UnscaledSize.X * window.Scale * 0.5f;
        var y = Lider.WindowY(w, window);
        var top = y + (window.UnscaledSize.Y + PanelView.TitleH) * 0.5f * window.Scale;
        var bottom = y - window.UnscaledSize.Y * 0.5f * window.Scale;
        var left = Lider.ViewportPoint(Space.At(angle - half / Space.R, top, Space.R));
        var right = Lider.ViewportPoint(Space.At(angle + half / Space.R, bottom, Space.R));
        Check(Near(right.X, X + W), "the window's right edge is at " + right.X + ", want " + (X + W));
        Check(left.X > X && left.Y >= Y - 2 && right.Y <= Y + H + 2, "the window is outside the area: " + left.X + "," + left.Y + " - " + right.X + "," + right.Y);
        var board = Lider.BoardViewport();
        Check(board.X >= X - 2 && board.Z <= left.X && board.Y >= Y - 2 && board.W <= Y + H + 2,
            "the inspector is outside the area or over the window: " + board.X + "," + board.Y + " - " + board.Z + "," + board.W + " (window from " + left.X + ")");
        // 止めるには控えた frame が要るので、ゲームが動き始めるまで待つ
        if (failed || GameApp.Frames == 0)
            return;
        pausing = true;
    }

    static int after;

    // 止めて、控えた frame の帯の場所を確かめる
    static void Pause()
    {
        after++;
        if (after == 2)
        {
            Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = Keys.F5 });
            Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = Keys.F5 });
            return;
        }
        if (after < 5)
            return;
        var band = Lider.TimelineViewport();
        var fail = GameApp.Position != 0 ? "F5 did not pause"
            : band.X < X - 2 || band.Z > X + W + 2 || band.Y < Y + H - 60 || band.W > Y + H + 2
            ? "the timeline is not at the bottom of the area: " + band.X + "," + band.Y + " - " + band.Z + "," + band.W : "";
        Console.WriteLine(fail == "" ? "PASS" : "FAIL " + fail);
        App.Quit(fail == "" ? 0 : 1);
    }

    static bool Near(float a, float b)
    {
        return Math.Abs(a - b) < 4;
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
