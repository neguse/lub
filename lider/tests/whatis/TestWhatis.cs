// Pointing at a game object shows the code that drew it in What. Screen coordinates are converted to MainTex pixels for the lookup (high DPI on the web).
// The object is a square that the game (game/Game.cs) draws in the middle of the screen.
//   - If the pixel ratio is 2, the game draws to a texture with twice the screen's pixels, and the middle of the screen looks up the middle of that texture (without the ratio, it would look up the 1/4 point and miss)
//   - Pointing at the object lists "method (file:line)" entries in What, from the innermost. Empty where there is nothing
//   - Middle-clicking opens that line
// Frames are counted from when the game starts running.
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestWhatis
{
    static int frames;
    static int stage;
    static int nextAt = 25;
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
        // Change the ratio before pointing, so that the game's texture is recreated
        Lider.PixelRatio = 2;
        // Wait for the game build
        if (failed || GameApp.Frames == 0)
            return;
        frames++;
        if (frames > 400)
        {
            Check(false, "stage " + stage + " did not finish: " + string.Join(" / ", Lider.What));
            return;
        }
        if (frames < nextAt)
            return;
        Gfx.Size(out var w, out var h);
        // Middle of the game view (depending on the arrangement, the view covers only part of the screen)
        var picture = Lider.Screens[0].Windows[0].ContentRect;
        var at = picture != null ? new Vec2(picture.X + picture.W * 0.5f, picture.Y + picture.H * 0.5f) : new Vec2(w / 2, h / 2);
        var what = string.Join(" / ", Lider.What);
        if (stage == 0)
        {
            // The code of the pointed object can be inspected or opened only while the game is paused
            Lider.TogglePause();
            Lider.HoverAt(at.X, at.Y);
            stage = 1;
        }
        else if (stage == 1 && what != "")
        {
            Check(what.StartsWith("Game.Quad (game/Game.cs:"), "what: " + what);
            Check(what.Contains(" / ← Game.OnFrame (game/Game.cs:"), "the caller is missing: " + what);
            // Top-left corner of the view (where there is no object)
            Lider.HoverAt(at.X - (picture != null ? picture.W : w) * 0.5f + 1, at.Y - (picture != null ? picture.H : h) * 0.5f + 1);
            stage = 2;
            nextAt = frames + 10;
        }
        else if (stage == 2)
        {
            Check(what == "", "the empty place has a what: " + what);
            // The footprint board is in front, so use a middle click, which picks the game object behind it
            Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = 2, X = at.X, Y = at.Y });
            stage = 3;
        }
        else if (stage == 3 && Lider.Work!.Focus != null)
        {
            Check(Lider.Work.Focus.Path.EndsWith("game/Game.cs"), "the click opened " + Lider.Work.Focus.Path);
            if (failed)
                return;
            Console.WriteLine("PASS");
            App.Quit(0);
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
