// Takes the GameView window out of the Screen's order and places it in Space.
//   - While Alt is held, the title bar appears even with only one game view, and pressing the marker on the bar moves it out to Space
//   - A game placed in Space is in no Screen's order, but it keeps running and is drawn filling the screen
//   - Pressing the Space tag that appears while Alt is held puts it back at the same position in the order of the original Screen
//   - Recording the arrangement also writes the things placed in Space to the file, and restoring that arrangement places them in Space
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestInSpace
{
    static int frames;
    static int stage;
    static float waited;
    static bool failed;
    static int framesBefore;

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
        // Wait for the game build
        if (failed || GameApp.Frames == 0)
            return;
        frames++;
        var w = Lider.Work!;
        var backdrop = Lider.Screens[0];
        if (frames < 6)
            return;
        waited += dt;
        if (waited < 0.4f)
            return;
        waited = 0;
        switch (stage++)
        {
            case 0:
                Check(backdrop.Windows[0].Content is GameView && w.InSpace == null, "the game is not on the backdrop at first");
                break;
            case 1:
                var game = backdrop.Windows[0];
                var bar = game.ViewportRect;
                Check(bar != null && game.TitleBar > 0, "the game window has no title bar");
                if (failed)
                    return;
                // The marker at the right end of the bar (the saved version has no close marker, so the right end is the marker that moves it out to Space)
                Press(bar!.X + bar.W - game.TitleBar * 0.5f, bar.Y + game.TitleBar * 0.5f);
                Check(w.InSpace == game && w.ScreenOf(game) == null, "pressing the mark did not put the game in the space");
                framesBefore = GameApp.Frames;
                break;
            case 2:
                var spaced = w.InSpace!;
                Check(GameApp.Frames > framesBefore, "the game stopped in the space");
                Check(spaced.ContentRect != null && spaced.ContentRect.X == 0 && Math.Abs(spaced.ContentRect.W - 1280) <= 1 && Math.Abs(spaced.ContentRect.H - 720) <= 1, "the game in the space does not fill the viewport");
                Check(GameApp.Map.W == (int)(1280 * Lider.PixelRatio), "the picture is " + GameApp.Map.W + " px wide");
                Check(Lider.SpaceTagOf() == null, "the space tag is out without Alt");
                // Recording the arrangement also writes the things placed in Space
                Lider.SaveArrangement("spaced");
                Io.LoadText(w.Root + "/.lider/arrangements.txt", out var text, out _, out _, out _);
                Check(text != null && text.Contains("arrangement spaced\nspace game:saved\n") && text.Contains("window game:saved\n"), "the file: " + text);
                var read = text != null ? Arrangement.Read(text) : new List<Arrangement>();
                Check(read.Count == 1 && read[0].Space == "game:saved" && string.Join("\n", read[0].Lines()) + "\n" == text, "reading the file back gave different lines");
                w.SetMods(Keys.ModAlt);
                break;
            case 3:
                var tag = Lider.SpaceTagOf();
                Check(tag != null, "the space tag is not out while Alt is down");
                if (failed)
                    return;
                var at = Lider.SurfacePx(backdrop, tag!.Value.X, tag.Value.Y);
                var was = w.InSpace!;
                Press(at.X, at.Y);
                Check(w.InSpace == null && backdrop.Windows[0] == was, "pressing the space tag did not put the game back first on the backdrop");
                w.SetMods(0);
                break;
            case 4:
                var back = backdrop.Windows[0];
                Check(back.ContentRect != null && Math.Abs(back.ContentRect.W - 640) <= 1, "the game is not the left half again");
                // Restoring the recorded arrangement places it in Space
                Check(Lider.Arrange("spaced") && w.InSpace == back && w.ScreenOf(back) == null, "the arrangement did not put the game in the space");
                Check(Lider.Arrange(Lider.SideBySide) && w.InSpace == null && backdrop.Windows[0] == back, "side-by-side did not take the game out of the space");
                if (!failed)
                {
                    Console.WriteLine("PASS");
                    App.Quit(0);
                }
                break;
        }
    }

    static void Press(float x, float y)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = 1, X = x, Y = y });
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonUp, Button = 1, X = x, Y = y });
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
