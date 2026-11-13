// focus のある窓の cursor の画面の位置 (Lider.CaretScreen。LIDER_CARET_LOG=1 なら標準出力にも書く)。
//   - 画面の中にあり、高さは行の高さほど
//   - 1 行下へ動かすと、同じ x で、行の高さだけ下へ動く
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestCaret
{
    static int frames;
    static bool failed;
    static Vec3 before;
    static float waited;
    static int stage;

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
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
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
            w.Focus!.MoveTo(3, 4);
        }
        // 窓が開いて止まるまで (2 秒) 待つ
        if (stage == 0 && frames > 2 && waited > 2)
        {
            stage = 1;
            waited = 0;
            before = Lider.CaretScreen;
            Gfx.Size(out var sw, out var sh);
            Check(before.X > 0 && before.X < sw && before.Y > 0 && before.Y < sh, "the caret is off the screen: " + before.X + "," + before.Y);
            Check(before.Z > 5 && before.Z < 60, "the caret height is " + before.Z);
            w.Focus!.MoveTo(4, 4);
        }
        if (stage == 1 && waited > 1)
        {
            stage = 2;
            var after = Lider.CaretScreen;
            Check(Math.Abs(after.X - before.X) < 1, "moving down changed x: " + before.X + " -> " + after.X);
            Check(Math.Abs(after.Y - before.Y - before.Z) < 2, "moving down one line moved y by " + (after.Y - before.Y) + ", the line is " + before.Z);
            if (failed)
                return;
            Console.WriteLine("PASS");
            App.Quit(0);
        }
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
