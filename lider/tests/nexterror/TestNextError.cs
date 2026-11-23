// Ctrl+E (code:next-error) jumps to compile error lines.
//   - Jumps to the error after the current one in the order of the error list, and after the last one goes back to the first. If the current line has no error, goes to the first
//   - For an error in a file without a tab, opens a tab
//   - Errors without a path are skipped. If there is no error to jump to, it does not move
//   - Ctrl+- goes back to the place before the jump
using System;
using Lub;
using LiderCore;

public static class TestNextError
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
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
    }

    static void TestFrame(float dt)
    {
        frames++;
        if (failed || frames != 3)
            return;
        var w = Lider.Work!;
        var target = Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!;
        var other = Environment.GetEnvironmentVariable("LIDER_ROOT") + "/TestNextError.cs";
        w.FocusOn(target);
        w.Focus!.MoveTo(40, 0);
        w.BuildFailed("broken without a place\n" + target + "(4,1): error CS0001: a\n" + target + "(8,3): error CS0002: b\n" + other + "(2,1): error CS0003: c\n");
        Check(w.Windows.Count == 1 && w.FocusWindow!.Code.Tabs.Count == 1, "the other file has a tab already");

        NextError();
        At(target, 3, "the first error from a line without an error");
        NextError();
        At(target, 7, "the next error in the same file");
        NextError();
        At(other, 1, "the error in a file without a window");
        Check(w.Windows.Count == 1 && w.FocusWindow!.Code.Tabs.Count == 2, "no tab opened for the other file");
        NextError();
        At(target, 3, "the first error after the last");

        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = Keys.LeftCtrl, Mods = Keys.ModCtrl, Typed = 0, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = Keys.Minus, Mods = Keys.ModCtrl, Typed = 0, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = Keys.LeftCtrl, Mods = 0, Typed = 0, Plain = 0 });
        At(other, 1, "Ctrl+- after Ctrl+E");

        w.BuildFailed("broken without a place\n");
        NextError();
        At(other, 1, "Ctrl+E without a place to go");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void NextError()
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = 8, Mods = Keys.ModCtrl, Typed = 0, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = 8, Mods = 0, Typed = 0, Plain = 0 });
    }

    static void At(string path, int line, string what)
    {
        var doc = Lider.Work!.Focus;
        Check(doc != null && doc.Path == path && doc.Line == line,
            what + ": at " + (doc == null ? "nothing" : doc.Path + ":" + doc.Line) + ", want " + path + ":" + line);
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
