// lub は宣言が途切れた texture を捨てるので、宣言が途切れた板は全部を描き直す。
//   - 窓を閉じて SweepFrames 待って開き直すと、全部を描き直す
//   - lider を隠して SweepFrames 待って出し直すと、全部を描き直す
//   - 途切れていなければ、打っても帯だけを描き直す (打って出る案内の字を、先に atlas へ入れてから測る)
using System;
using Lub;
using LiderCore;

public static class TestReopen
{
    // lub が捨てるまでの frame より長く待つ
    const int SweepFrames = 130;
    static int frames;
    static int stage;
    static int waitUntil;
    static bool failed;
    static string file = "";
    static CodeWindow? closed;

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

    static void TestFrame(float dt)
    {
        frames++;
        if (failed || frames < waitUntil)
            return;
        var w = Lider.Work!;
        switch (stage)
        {
            case 0:
                file = Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!;
                w.FocusOn(file);
                waitUntil = frames + 10;
                break;
            case 1:
                // 打つと案内 (F9) が出る。密度 1 では案内と窓が文字の atlas を分け合い、案内に初めて出る字で窓を全部描き直すので、先に出しておく
                w.Focus!.Type("x");
                waitUntil = frames + 3;
                break;
            case 2:
                Lider.Panel(w.FocusWindow!).RedrawnLines = Untouched;
                w.Focus!.Type("x");
                waitUntil = frames + 3;
                break;
            case 3:
                Check(Lider.Panel(w.FocusWindow!).RedrawnLines >= 0, "typing redrew everything: " + Lider.Panel(w.FocusWindow!).RedrawnLines);
                closed = w.FocusWindow;
                w.CloseTab();
                break;
            case 4:
                // 閉じる動きの間は宣言が続く
                if (Lider.MotionOf(closed!) != null)
                    return;
                Lider.Panel(closed!).RedrawnLines = Untouched;
                waitUntil = frames + SweepFrames;
                break;
            case 5:
                w.FocusOn(file);
                waitUntil = frames + 3;
                break;
            case 6:
                Check(Lider.Panel(w.FocusWindow!).RedrawnLines == -1, "reopening did not redraw everything: " + Lider.Panel(w.FocusWindow!).RedrawnLines);
                Lider.Panel(w.FocusWindow!).RedrawnLines = Untouched;
                Lider.SetActive(false);
                Lider.GameOnly = true;
                waitUntil = frames + SweepFrames;
                break;
            case 7:
                Lider.SetActive(true);
                waitUntil = frames + 3;
                break;
            default:
                Check(Lider.Panel(w.FocusWindow!).RedrawnLines == -1, "showing lider again did not redraw everything: " + Lider.Panel(w.FocusWindow!).RedrawnLines);
                if (failed)
                    return;
                Console.WriteLine("PASS");
                App.Quit(0);
                break;
        }
        stage++;
    }

    // 描き直していない印
    const int Untouched = -2;

    static void Check(bool ok, string message)
    {
        if (ok || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}
