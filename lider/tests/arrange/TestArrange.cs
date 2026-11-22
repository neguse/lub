// Records arrangements under names and restores them.
//   - At startup the side-by-side arrangement is used, and the built-in arrangements (side by side, overlay) can be restored with commands
//   - Recording the current arrangement writes it to a file, and after switching to another arrangement, the arrangement with that name can be restored (the screen rules and the window order)
//   - The name for recording is decided by typing it in the search field
//   - An arrangement reloaded from the file's lines is the same as the one written
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
                Check(Lider.ArrangementName == Lider.SideBySide && Lider.Tiled && w.ScreenOf(code) == backdrop, "not side-by-side at first");
                // Code panels opened after startup also go into the order decided by the arrangement (before the inspector)
                Check(Order(w, backdrop) == "game:saved code:Target.cs inspector", "the order at first: " + Order(w, backdrop));
                Check(Lider.Run("arrange:" + Lider.Overlay), "arrange:overlay did not run");
                Check(Lider.ArrangementName == Lider.Overlay && w.ScreenOf(code) == w.CodeScreen && w.ScreenOf(inspector) == w.CodeScreen, "overlay did not move the windows to the code screen");
                Check(Lider.Run("arrange:" + Lider.SideBySide), "arrange:side-by-side did not run");
                Check(Lider.Tiled && w.ScreenOf(code) == backdrop && w.ScreenOf(inspector) == backdrop, "side-by-side did not move the windows to the backdrop");
                Check(backdrop.Layout is MainStack && Order(w, backdrop) == "game:saved code:Target.cs inspector", "side-by-side order: " + Order(w, backdrop));
                break;
            case 1:
                // Change the order (inspector to the front) and the rule too, then record
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
                // The name is typed in the search field
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

    // Keys of the windows on the screen, in list order
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
