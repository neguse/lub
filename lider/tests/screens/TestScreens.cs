// Shows screen outlines and name tags, and moves a screen by grabbing its tag.
//   - Tags appear only while Alt is held
//   - Tags appear inside the display. Grabbing the tag of a movable screen with the right button and moving it makes the grabbed point follow the pointed point
//   - Turning the wheel while grabbing changes the depth
//   - After moving, windows can be pointed at where they are visible
//   - The tag of a screen that cannot be moved cannot be grabbed
//   - A command can change the anchor, and restoring the arrangement also restores the screen's position and anchor
using System;
using Lub;
using LiderCore;

public static class TestScreens
{
    static int frames;
    static int stage;
    static float waited;
    static bool failed;
    static float windowX;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 1280, Height = 720 });
        Lider.OnInit();
        // Check in the overlay layout (code windows lined up in a horizontal row on the cylindrical screen in space)
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
        if (frames == 2)
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
        if (frames < 6)
            return;
        waited += dt;
        if (waited < 0.4f)
            return;
        waited = 0;
        var code = w.CodeScreen;
        var window = w.FocusWindow!;
        switch (stage++)
        {
            case 0:
                Check(!Lider.ScreensLit && Lider.TagOf(code) == null, "the tags are out without Alt");
                w.SetMods(Keys.ModAlt);
                break;
            case 1:
                Check(Lider.ScreensLit && Lider.TagOf(code) != null && Lider.TagOf(Lider.Screens[0]) != null, "the tags are not out while Alt is down");
                // The tag of a screen that cannot be moved (the game view at the back) cannot be grabbed
                var fixedAt = TagPx(Lider.Screens[0]);
                Mouse(EventKind.MouseButtonDown, fixedAt.X, fixedAt.Y);
                Check(Lider.HeldScreen == null, "a screen that cannot move was grabbed");
                Mouse(EventKind.MouseButtonUp, fixedAt.X, fixedAt.Y);
                var at = TagPx(code);
                Check(at.X > 0 && at.X < 1280 && at.Y > 0 && at.Y < 720, "the tag of the code screen is off the viewport: " + at.X + " " + at.Y);
                // The grabbed point (a point on the screen's surface) follows the pointed point
                var held = Lider.TagOf(code)!.Value;
                Mouse(EventKind.MouseButtonDown, at.X, at.Y);
                Check(Lider.HeldScreen == code, "the code screen was not grabbed by its tag");
                Mouse(EventKind.MouseMotion, at.X + 100, at.Y + 50);
                var moved = Lider.SurfacePx(code, held.X, held.Y);
                Near(moved.X, at.X + 100, 1.5f, "the held point x after the drag");
                Near(moved.Y, at.Y + 50, 1.5f, "the held point y after the drag");
                Check(code.Position.Y < 0 && code.Angle > 0, "the screen did not move right and down: " + code.Angle + " " + code.Position.Y);
                Near(code.Radius, Space.R, 0.001f, "the depth after the drag");
                // Turning the wheel toward you moves it farther away
                Lider.OnEvent(new EventData { Kind = EventKind.MouseWheel, Dy = -2 });
                Near(code.Radius, Space.R * 1.21f, 0.001f, "the depth after the wheel");
                Mouse(EventKind.MouseButtonUp, at.X + 100, at.Y + 50);
                Check(Lider.HeldScreen == null, "the screen is still held after the release");
                w.SetMods(0);
                Check(!Lider.ScreensLit, "the tags are out after Alt went up");
                break;
            case 2:
                // Grab the moved window at its visible center and drag it to the right
                var center = Lider.ViewportPoint(Space.At(Lider.WindowAngle(w, window), Lider.WindowY(w, window), Space.R));
                Check(center.X > 0 && center.X < 1280 && center.Y > 0 && center.Y < 720, "the window is off the viewport: " + center.X + " " + center.Y);
                windowX = window.Position!.Value.X;
                Mouse(EventKind.MouseButtonDown, center.X, center.Y);
                Mouse(EventKind.MouseMotion, center.X + 60, center.Y);
                Check(window.Position!.Value.X > windowX + 0.01f, "the window was not grabbed where it is drawn");
                Mouse(EventKind.MouseButtonUp, center.X + 60, center.Y);
                break;
            case 3:
                Check(code.Anchor == ScreenAnchor.World && Lider.Run("screen:anchor") && code.Anchor == ScreenAnchor.View, "screen:anchor did not fix the screen to the view");
                break;
            case 4:
                var fixedCenter = Lider.ViewportPoint(Space.At(Lider.WindowAngle(w, window), Lider.WindowY(w, window), Space.R));
                Check(fixedCenter.X > 0 && fixedCenter.X < 1280 && fixedCenter.Y > 0 && fixedCenter.Y < 720, "the window is off the viewport on the view: " + fixedCenter.X + " " + fixedCenter.Y);
                // Restoring the arrangement also restores the screen's position and anchor
                Check(Lider.Run("arrange:" + Lider.Overlay), "arrange:overlay did not run");
                Check(code.Anchor == ScreenAnchor.World, "the arrangement did not put the screen back on the world");
                Near(code.Radius, Space.R, 0.001f, "the depth after the arrangement");
                Near(code.Position.Y, 0, 0.001f, "the height after the arrangement");
                if (!failed)
                {
                    Console.WriteLine("PASS");
                    App.Quit(0);
                }
                break;
        }
    }

    // Display px of the center of screen's name tag
    static Vec2 TagPx(Screen screen)
    {
        var tag = Lider.TagOf(screen)!.Value;
        return Lider.SurfacePx(screen, tag.X, tag.Y);
    }

    static void Mouse(EventKind kind, float x, float y)
    {
        Lider.OnEvent(new EventData { Kind = kind, Button = 3, X = x, Y = y });
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
