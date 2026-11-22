// Display position of the cursor in the focused panel (Lider.CaretViewport; also written to standard output if LIDER_CARET_LOG=1).
//   - It is inside the display, and its height is about the line height
//   - Moving it down 1 line moves it down by the line height, at the same x
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
        // Wait until the panel opens and stops (2 seconds)
        if (stage == 0 && frames > 2 && waited > 2)
        {
            stage = 1;
            waited = 0;
            before = Lider.CaretViewport;
            Gfx.Size(out var sw, out var sh);
            Check(before.X > 0 && before.X < sw && before.Y > 0 && before.Y < sh, "the caret is off the viewport: " + before.X + "," + before.Y);
            Check(before.Z > 5 && before.Z < 60, "the caret height is " + before.Z);
            w.Focus!.MoveTo(4, 4);
        }
        if (stage == 1 && waited > 1)
        {
            stage = 2;
            var after = Lider.CaretViewport;
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
