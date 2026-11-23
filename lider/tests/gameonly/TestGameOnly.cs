// Shows only the game view (command screen:hide).
//   - In the side-by-side layout, the code windows are lined up on the same screen as the game view
//   - Showing only the game view makes the view fill the screen, hides the other windows, and moves focus to the game. The order is not changed
//   - Moving focus back to Code shows them in the original order
using System;
using Lub;
using LiderCore;

public static class TestGameOnly
{
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
        var game = backdrop.Windows[0];
        var code = w.FocusWindow!;
        switch (stage++)
        {
            case 0:
                Check(game.Content is GameView && backdrop.Windows.Count == 3 && w.ScreenOf(code) == backdrop, "not side-by-side at first");
                Check(game.ContentRect != null && Math.Abs(game.ContentRect.W - 640) <= 1 && code.ContentRect != null, "the game is not the left half at first");
                Check(Lider.Run("screen:hide") && Lider.Hidden && w.OnGame, "screen:hide did not leave the game only");
                break;
            case 1:
                Check(game.ContentRect != null && game.ContentRect.X == 0 && game.ContentRect.Y == 0 && Math.Abs(game.ContentRect.W - 1280) <= 1 && Math.Abs(game.ContentRect.H - 720) <= 1,
                    "the game does not fill the viewport: " + (game.ContentRect != null ? game.ContentRect.W + "x" + game.ContentRect.H : "none"));
                Check(code.Position == null && code.ContentRect == null && Lider.InspectWindow.Position == null, "the other windows are still out");
                Check(backdrop.Windows[0] == game && backdrop.Windows[1] == code && backdrop.Windows[2] == Lider.InspectWindow, "the order changed while hidden");
                Lider.FocusCode();
                Check(!Lider.Hidden && !w.OnGame, "the focus did not come back to the code");
                break;
            case 2:
                Check(game.ContentRect != null && Math.Abs(game.ContentRect.W - 640) <= 1, "the game did not go back to the left half");
                Check(code.Position != null && code.ContentRect != null && Lider.InspectWindow.Position != null, "the other windows did not come back");
                Check(backdrop.Windows[0] == game && backdrop.Windows[1] == code && backdrop.Windows[2] == Lider.InspectWindow, "the order changed after showing again");
                if (!failed)
                {
                    Console.WriteLine("PASS");
                    App.Quit(0);
                }
                break;
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
