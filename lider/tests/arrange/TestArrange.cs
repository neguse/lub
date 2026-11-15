// 配置を、名前を付けて控えて戻す。
//   - 初めから入っている配置 (重ねる形、左右に並ぶ形) に、命令で戻せる
//   - 今の配置を控えると file に書かれ、ほかの配置にした後で、その名前の配置に戻せる (スクリーンの規則と、ウィンドウの並び)
//   - 控えるときの名前は、検索の欄に打って決める
//   - file の行から読み直した配置は、書いた配置と同じ
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestArrange
{
    const int A = 4;
    static int frames;
    static int stage;
    static float waited;
    static bool failed;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 1280, Height = 720 });
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

    static void TestFrame(float dt)
    {
        if (failed)
            return;
        frames++;
        var w = Lider.Work!;
        var backdrop = Lider.Screens[0];
        if (frames == 2)
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
        if (frames < 6)
            return;
        waited += dt;
        if (waited < 0.4f)
            return;
        waited = 0;
        var code = w.FocusWindow!;
        var inspector = Lider.InspectWindow;
        switch (stage++)
        {
            case 0:
                Check(Lider.ArrangementName == Lider.Overlay && w.ScreenOf(code) == w.CodeScreen && w.ScreenOf(inspector) == w.CodeScreen, "not the overlay at first");
                Check(Lider.Run("arrange:" + Lider.SideBySide), "arrange:side-by-side did not run");
                Check(Lider.Tiled && w.ScreenOf(code) == backdrop && w.ScreenOf(inspector) == backdrop, "side-by-side did not move the windows to the backdrop");
                Check(backdrop.Layout is MainStack && Order(w, backdrop) == "game:saved code:Target.cs inspector", "side-by-side order: " + Order(w, backdrop));
                break;
            case 1:
                // 並びを変えて (インスペクタを前へ)、規則も変えてから控える
                backdrop.Windows.Remove(inspector);
                backdrop.Windows.Insert(1, inspector);
                backdrop.Layout = new Even { Rows = true };
                Lider.SaveArrangement("mine");
                Io.LoadText(w.Root + "/.lider/arrangements.txt", out var text, out _, out _, out _);
                Check(text != null && text.Contains("arrangement mine\n") && text.Contains(" Even rows\n") && text.Contains("window inspector\n"), "the file: " + text);
                var read = text != null ? Arrangement.Read(text) : new List<Arrangement>();
                Check(read.Count == 1 && string.Join("\n", read[0].Lines()) + "\n" == text, "reading the file back gave different lines");
                Check(Lider.Run("arrange:" + Lider.Overlay), "arrange:overlay did not run");
                Check(!Lider.Tiled && w.ScreenOf(code) == w.CodeScreen && w.ScreenOf(inspector) == w.CodeScreen && backdrop.Layout is Even,
                    "the overlay did not move the windows back");
                Check(Order(w, w.CodeScreen) == "inspector code:Target.cs", "overlay order: " + Order(w, w.CodeScreen));
                break;
            case 2:
                Check(Lider.Run("arrange:mine"), "arrange:mine did not run");
                Check(backdrop.Layout is Even rows && rows.Rows && Order(w, backdrop) == "game:saved inspector code:Target.cs", "mine: " + Order(w, backdrop));
                Check(Lider.ArrangementName == "mine" && Lider.Tiled, "the home of the code windows did not come back");
                // 名前は検索の欄に打つ
                Check(Lider.Run("arrange:save") && Lider.Search.Shown && Lider.Search.Prompt != "", "arrange:save did not ask for a name");
                Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = A, Mods = 0, Typed = 'a', Plain = 'a' });
                Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = Keys.Return, Mods = 0, Typed = 0, Plain = 0 });
                Check(!Lider.Search.Shown && Lider.ArrangementName == "a" && Lider.Run("arrange:a"), "typing a name did not save the arrangement");
                if (!failed)
                {
                    Console.WriteLine("PASS");
                    App.Quit(0);
                }
                break;
        }
    }

    // スクリーンの上のウィンドウの key を、並びの順に
    static string Order(Workspace w, Screen screen)
    {
        var keys = new List<string>();
        foreach (var window in screen.Windows)
            keys.Add(Arrangement.KeyOf(w, window));
        return string.Join(" ", keys);
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
