// 操作は名前の付いた命令で、key と検索から同じ命令を呼ぶ。
//   - Ctrl+P の検索に命令が並び、">" で始めると命令だけになる。確定すると実行する
//   - Alt+矢印で、見た目で隣にあるウィンドウへ focus が移る (インスペクタとゲームの絵も含む)
//   - focus がゲームにある間は、focus を戻す key のほかは命令にならない
//   - 検索はスクリーンの上の真ん中のウィンドウで、出している間は focus がそこにあり、やめると前のウィンドウに、確定すると Code に戻る
using System;
using Lub;
using LiderCore;

public static class TestCommands
{
    const int I = 12;
    const int P = 19;
    static int frames;
    static int stage;
    static float waited;
    static bool failed;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 1280, Height = 720 });
        Lider.OnInit();
        // 重ねる形 (コードの窓を、空間の円筒のスクリーンに横一列に並べる形) で確かめる
        Lider.Arrange(Lider.Overlay);
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
        var inspector = Lider.InspectWindow;
        if (frames == 2)
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
        if (frames < 6)
            return;
        // 窓と視点が動き終わるのを待つ
        waited += dt;
        if (waited < 0.6f)
            return;
        waited = 0;
        switch (stage++)
        {
            case 0:
                Key(P, Keys.ModCtrl, 0);
                Check(Lider.Search.Shown, "Ctrl+P did not open the search");
                Type(">inspector:toggle");
                Check(Lider.Search.Results.Count > 0 && Lider.Search.Results[0].Command == "inspector:toggle",
                    "the search did not find the command: " + (Lider.Search.Results.Count > 0 ? Lider.Search.Results[0].Text : "none"));
                Check(Lider.Search.Results[0].Where.Contains("[alt+i]"), "the key is not shown: " + Lider.Search.Results[0].Where);
                foreach (var item in Lider.Search.Results)
                    Check(item.Command != "", "a file is listed after '>': " + item.Text);
                Key(Keys.Return, 0, 0);
                Check(!Lider.Search.Shown && w.Focused == inspector, "committing the command did not run it");
                break;
            case 1:
                // インスペクタはコードの窓の左にある
                Key(Keys.Right, Keys.ModAlt, 0);
                Check(w.Focused == w.FocusWindow, "Alt+Right from the inspector did not move to the code");
                Key(Keys.Left, Keys.ModAlt, 0);
                Check(w.Focused == inspector, "Alt+Left from the code did not move to the inspector");
                Key(Keys.F1, 0, 0);
                Check(w.OnGame, "F1 did not move the focus to the game");
                // focus がゲームにある間、Alt+I はゲームに渡る
                Key(I, Keys.ModAlt, 0);
                Check(w.OnGame && Lider.Inspector.Shown, "a key other than F1 ran a command while the game had the focus");
                Key(Keys.F1, 0, 0);
                Check(!w.OnGame, "F1 did not bring the focus back");
                break;
            case 2:
                Check(Lider.Run("game:pause") && GameApp.Position >= -1, "game:pause did not run");
                Check(!Lider.Run("no:such-command"), "an unknown command ran");
                // 検索はウィンドウで、出している間は focus がそこにある
                Key(I, Keys.ModAlt, 0);
                Check(w.Focused == inspector, "Alt+I did not move the focus to the inspector");
                Key(P, Keys.ModCtrl, 0);
                Check(Lider.Search.Shown && w.Focused == Lider.SearchWindow, "the search did not take the focus");
                break;
            case 3:
                Gfx.Size(out var sw, out var sh);
                var at = Lider.SearchWindow.ViewportRect;
                Check(at != null && Math.Abs(at.X + at.W * 0.5f - sw * 0.5f) <= 1 && at.Y > 0 && at.Y < sh * 0.1f,
                    "the search is not at the top center: " + (at != null ? at.X + "," + at.Y + " " + at.W + "x" + at.H : "none"));
                Key(Keys.Escape, 0, 0);
                Check(!Lider.Search.Shown && w.Focused == inspector, "cancelling the search did not return the focus to the inspector");
                Key(P, Keys.ModCtrl, 0);
                Type("target");
                Key(Keys.Return, 0, 0);
                Check(!Lider.Search.Shown && w.Focused == w.FocusWindow, "committing the search did not move the focus to the code");
                break;
            case 4:
                Check(Lider.SearchWindow.Position == null, "the closed search is still on the screen");
                if (!failed)
                {
                    Console.WriteLine("PASS");
                    App.Quit(0);
                }
                break;
        }
    }

    // 検索の欄に打つ (字ごとに、その字を打った key として渡す)
    static void Type(string text)
    {
        foreach (var c in Utf8.ByteList(text))
            Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = 0, Mods = 0, Typed = c, Plain = c });
    }

    static void Key(int key, int mods, int typed)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = mods, Typed = typed, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = 0, Typed = 0, Plain = 0 });
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
