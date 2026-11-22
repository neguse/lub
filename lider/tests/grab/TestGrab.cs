// Grabbing and moving windows, spreading them with both hands (Grab), hit testing of windows whose height and size were changed, and moving with the keyboard.
//   - Grabbing with one hand and moving the pointed point makes the window follow, keeping the offset between the grabbed point and the window center
//   - Doubling the distance between both hands doubles the window, stopping at Grab.MaxScale and MinScale
//   - Going back from both hands to one hand grabs again from that point and moves
//   - A window whose height and size were changed is hit by the ray at that position and size
//   - Alt + PageUp makes it larger
//   - Outside XR, the cylinder is unrolled onto a plane and windows open large. Right-dragging moves a window, the wheel during a drag changes its size, and releasing puts it back at its position in the row. Right-dragging outside a window does not grab the window
using System;
using Lub;
using LiderCore;

public static class TestGrab
{
    static int frame;
    static bool failed;

    public static void OnInit()
    {
        TestInit();
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

    static void TestInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    static void Near(float got, float want, string what)
    {
        if (Math.Abs(got - want) > 0.001f)
            Check(false, what + ": " + got + ", want " + want);
    }

    static void TestFrame(float dt)
    {
        frame++;
        if (frame == 1)
            Curved();
        else if (frame == 2)
            Sized();
        else if (frame == 4)
            Dragged();
    }

    static void Curved()
    {
        var target = Environment.GetEnvironmentVariable("LIDER_TEST_FILE");
        var w = Lider.Work;
        Check(target != null && w != null, "LIDER_TEST_FILE or the workspace is missing");
        if (failed)
            return;
        // Put the inspector away so the window is placed in the middle of the screen
        Lider.Inspector.Shown = false;
        w!.FocusOn(target!);
        var window = w.FocusWindow!;
        // The grab position and the move target are (x, y) on the surface of the code screen
        window.Position = new Vec3(0.2f, 0.1f, 0);
        window.Size = window.UnscaledSize;

        var grab = new Grab();
        grab.Start(window, 0.25f, 0.0f);
        grab.Move(0.45f, -0.2f);
        Near(window.Position!.Value.X, 0.4f, "x after a one-hand move");
        Near(window.Position!.Value.Y, -0.1f, "height after a one-hand move");

        grab.StartTwo(new Vec3(-0.1f, 0, 0.3f), new Vec3(0.1f, 0, 0.3f));
        grab.MoveTwo(new Vec3(-0.2f, 0, 0.3f), new Vec3(0.2f, 0, 0.3f));
        Near(window.Scale, 2, "scale after doubling the hands' distance");
        grab.Move(0.9f, 0.9f);
        Near(window.Position!.Value.X, 0.4f, "a one-hand move during a two-hand grab");
        grab.MoveTwo(new Vec3(-5, 0, 0.3f), new Vec3(5, 0, 0.3f));
        Near(window.Scale, Grab.MaxScale, "scale clamped at the maximum");
        grab.MoveTwo(new Vec3(-0.001f, 0, 0.3f), new Vec3(0.001f, 0, 0.3f));
        Near(window.Scale, Grab.MinScale, "scale clamped at the minimum");

        grab.ReleaseTwo(0.5f, 0.0f);
        grab.Move(0.6f, 0.1f);
        Near(window.Position!.Value.X, 0.5f, "x after going back to one hand");
        Near(window.Position!.Value.Y, 0.0f, "height after going back to one hand");
        grab.Release();
        Check(!grab.Holding, "still holding after release");

        // Near the top edge of a window with height 0.2 and size 1.5, the ray hits even at a height that would miss at size 1
        window.Scale = 1.5f;
        var edge = 0.2f + PanelView.BaseH * 1.5f * 0.45f;
        var ray = new Ray(new Vec3(0, 0, 0), new Vec3((float)Math.Sin(0.5f) * Space.R, edge, (float)Math.Cos(0.5f) * Space.R));
        Check(Space.HitCurved(ray, 0.5f, 0.2f, Space.R, PanelView.BaseW * window.Scale, PanelView.BaseH * window.Scale) != null,
            "the scaled and raised window was not hit near its top");
        Check(Space.HitCurved(ray, 0.5f, 0.2f, Space.R, PanelView.BaseW, PanelView.BaseH) == null,
            "an unscaled window was hit where only the scaled one should be");

        window.Position = null;
        window.Scale = 1;
        Lider.Run("window:grow");
        Near(window.Scale, 1.1f, "scale after Alt+PageUp");
        window.Position = null;
    }

    // After going through one lider frame, the window becomes a readable size on the non-XR plane
    static void Sized()
    {
        var window = Lider.Work!.FocusWindow!;
        Check(Space.Flat, "not flat without XR");
        Near(window.Scale, Space.FlatScale, "scale of a window opened without XR");
        window.Scale = 1;

        // The point at angle a and height y is at x = R * a, y on the plane
        var a = 0.25f;
        var ray = new Ray(new Vec3(0, 0, 0), new Vec3(a * Space.R, 0.2f, Space.R));
        var at = Space.OnCylinderAt(ray, Space.R);
        Near(at!.Angle, a, "angle on the plane");
        Near(at.Y, 0.2f, "height on the plane");
        var uv = Space.HitCurved(ray, a, 0.2f, Space.R, PanelView.BaseW, PanelView.BaseH);
        Near(uv!.Value.X, 0.5f, "u at the center of a flat window");
        Near(uv.Value.Y, 0.5f, "v at the center of a flat window");
    }

    static void Dragged()
    {
        var window = Lider.Work!.FocusWindow!;
        Gfx.Size(out var sw, out var sh);
        float cx = sw * 0.5f;
        float cy = sh * 0.5f;

        Mouse(EventKind.MouseButtonDown, 1, 1);
        Mouse(EventKind.MouseMotion, cx, cy);
        Near(Lider.WindowAngle(Lider.Work!, window), 0, "right drag from outside the window moved it");
        Mouse(EventKind.MouseButtonUp, cx, cy);

        Mouse(EventKind.MouseButtonDown, cx, cy);
        Mouse(EventKind.MouseMotion, cx + sw * 0.1f, cy - sh * 0.1f);
        Check(Lider.WindowAngle(Lider.Work!, window) > 0.05f, "right drag to the right: angle " + Lider.WindowAngle(Lider.Work!, window));
        Check(Lider.WindowY(Lider.Work!, window) > 0.05f, "right drag up: y " + Lider.WindowY(Lider.Work!, window));
        Lider.OnEvent(new EventData { Kind = EventKind.MouseWheel, Dy = 1 });
        Near(window.Scale, 1.1f, "scale after the wheel during a right drag");
        Mouse(EventKind.MouseButtonUp, cx + sw * 0.1f, cy - sh * 0.1f);

        Lider.Work!.Arrange(null);
        Near(Lider.WindowAngle(Lider.Work!, window), 0, "the only window did not go back to its place after the release");
        Near(Lider.WindowY(Lider.Work!, window), 0, "the window did not go back to the row after the release");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void Mouse(EventKind kind, float x, float y)
    {
        Lider.OnEvent(new EventData { Kind = kind, Button = 3, X = x, Y = y });
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
