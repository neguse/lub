// Windows of any content can be placed on any screen.
//   - Even when the game image window is moved to the cylinder screen in space, the game advances and the image is drawn with pixels matching the window size
//   - Pointing at a game image placed in space passes the position within that image to the game
//   - When the inspector is moved to another screen, it stays there
using System;
using Lub;
using LiderCore;

public static class TestAnywhere
{
    static int frames;
    static int stage;
    static int framesBefore;
    static bool failed;
    static Window? game;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 1280, Height = 720 });
        Lider.OnInit();
        // Check in the overlay arrangement (the code panels laid out in a single row on the cylinder screen in space)
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
        TestFrame();
        Lider.OnFrame(dt);
    }

    static void TestFrame()
    {
        // Wait for the game build
        if (failed || GameApp.Frames == 0)
            return;
        frames++;
        if (frames % 10 != 0)
            return;
        var w = Lider.Work!;
        switch (stage++)
        {
            case 0:
                game = Lider.Screens[0].Windows[0];
                Check(game.Content is GameView && game.ContentRect != null, "the game is not on the backdrop at first");
                Lider.MoveToNextScreen(w, game);
                Check(w.ScreenOf(game) == w.CodeScreen, "the game did not move to the code screen");
                framesBefore = GameApp.Frames;
                break;
            case 1:
                Check(game!.Position != null && game.ContentRect == null, "the game is not placed on the code screen");
                Check(GameApp.Frames > framesBefore, "the game stopped after it moved");
                var px = (int)(game.Size.X * PanelView.BasePxW / PanelView.BaseW * Lider.PixelRatio);
                Check(game.Size.X > 0 && GameApp.Map.W == px, "the picture is " + GameApp.Map.W + " px wide, want " + px);
                // Point slightly to the bottom right of the image's middle
                var at = Lider.ViewportPoint(Space.At(Lider.WindowAngle(w, game) + game.Size.X * 0.25f / Space.R, Lider.WindowY(w, game) - game.Size.Y * 0.25f, Space.R));
                Lider.OnEvent(new EventData { Kind = EventKind.MouseMotion, X = at.X, Y = at.Y });
                Near(GameApp.Map.X, GameApp.Map.W * 0.75f, GameApp.Map.W * 0.02f, "the pointed x in the picture");
                Near(GameApp.Map.Y, GameApp.Map.H * 0.75f, GameApp.Map.H * 0.02f, "the pointed y in the picture");
                // Move the inspector to the next screen
                w.FocusTo(Lider.InspectWindow);
                Check(Lider.Run("window:move-to-next-screen"), "window:move-to-next-screen did not run");
                Check(w.ScreenOf(Lider.InspectWindow) == Lider.Screens[0], "the inspector did not move to the backdrop");
                break;
            case 2:
                Check(w.ScreenOf(Lider.InspectWindow) == Lider.Screens[0], "the inspector did not stay on the backdrop");
                if (!failed)
                {
                    Console.WriteLine("PASS");
                    App.Quit(0);
                }
                break;
        }
    }

    static void Near(float got, float want, float within, string message)
    {
        Check(Math.Abs(got - want) <= within, message + ": " + got + ", want " + want);
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
