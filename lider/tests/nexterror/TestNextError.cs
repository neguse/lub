// F8 で compile error の行へ飛ぶ。
//   - error の並びの順に、今いる error の次へ飛び、最後の次は最初へ戻る。今いる行に error が無ければ最初へ
//   - タブの無い file の error ならタブを開く
//   - path の無い error は飛ばす。飛べる error が無ければ動かない
//   - Ctrl+- で飛ぶ前の場所へ戻る
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

        F8();
        At(target, 3, "the first error from a line without an error");
        F8();
        At(target, 7, "the next error in the same file");
        F8();
        At(other, 1, "the error in a file without a window");
        Check(w.Windows.Count == 1 && w.FocusWindow!.Code.Tabs.Count == 2, "no tab opened for the other file");
        F8();
        At(target, 3, "the first error after the last");

        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = Keys.LeftCtrl, Mods = Keys.ModCtrl, Typed = 0, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = Keys.Minus, Mods = Keys.ModCtrl, Typed = 0, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = Keys.LeftCtrl, Mods = 0, Typed = 0, Plain = 0 });
        At(other, 1, "Ctrl+- after F8");

        w.BuildFailed("broken without a place\n");
        F8();
        At(other, 1, "F8 without a place to go");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void F8()
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = Keys.F8, Mods = 0, Typed = 0, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = Keys.F8, Mods = 0, Typed = 0, Plain = 0 });
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
