// Checks that clicking a game object opens the code line that drew it beside the object.
// The game (game/Game.cs) draws one board in the center of the screen (Gfx.Draw on the DrawLine line).
// Clicks are fed to Lider.OnEvent, the same as events that come from lub.
// Hide lider and click the center, and check that the click belongs to the game and lider opens nothing.
// Show lider, left-click the center, and wait until the enlarged file is at the DrawLine line of game/Game.cs.
// The board is drawn by a Quad instance (Game.Shown) in its own Draw, so opening it narrows the inspector to that instance.
// Next, place the opened board in front (where the board hides what is behind it), and check that a middle click can pick the object behind the board again (a left click would move the cursor on the board).
// The line number is a position in game/Game.cs, so if lines are added there, update DrawLine to match.
// Frames are counted from when the game starts running.
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestPick
{
    const int DrawLine = 11;
    static int frames;
    static float waited;
    static int phase;
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
        // Wait for the game build
        if (GameApp.Frames == 0 || failed)
            return;
        frames++;
        Gfx.Size(out var w, out var h);
        // Middle of the game view (depending on the arrangement, the view covers only part of the screen)
        var picture = Lider.Screens[0].Windows[0].ContentRect;
        var at = picture != null ? new Vec2(picture.X + picture.W * 0.5f, picture.Y + picture.H * 0.5f) : new Vec2(w / 2, h / 2);
        if (frames == 3)
        {
            Lider.FocusGame();
            Click(1, at.X, at.Y);
        }
        // Game objects can be pointed at and inspected only while paused
        if (frames == 13)
        {
            Lider.FocusCode();
            Lider.TogglePause();
        }
        if (frames < 23)
        {
            Check(Lider.Work?.Focus == null, "a click on the running game opened " + Lider.Work?.Focus?.Path);
            return;
        }
        if (frames == 23)
            Click(1, at.X, at.Y);
        if (frames <= 23)
            return;
        waited += dt;
        var focus = Lider.Work?.Focus;
        if (focus == null || !focus.Path.EndsWith("Game.cs"))
        {
            Check(waited < 30, "the drawing code was not opened in 30 seconds");
            return;
        }
        var w0 = Lider.Work!;
        if (phase == 1 && focus.Line == 0)
        {
            Check(waited < 30, "the middle click did not pick through the panel in 30 seconds");
            return;
        }
        Check(focus.Line == DrawLine - 1, "opened line " + (focus.Line + 1) + ", want " + DrawLine);
        if (failed)
            return;
        if (phase == 0)
        {
            // While paused, the state is reloaded every frame, but instance ids do not change, so it can narrow to the pressed instance
            var shown = "";
            foreach (var n in GameApp.Inspect("Game", 0, 10))
                if (n.Name == "Shown")
                    shown = n.Id;
            Check(shown != "" && Lider.Inspector.Tree.ScopeId == shown && Lider.Inspector.Tree.ScopeClass == "Quad", "opening did not scope to the drawing quad: " + string.Join("|", Lider.Inspector.Tree.Crumbs()));
            phase = 1;
            waited = 0;
            focus.MoveTo(0, 0);
            Click(2, at.X, at.Y);
            return;
        }
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void Click(int button, float x, float y)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = button, X = x, Y = y });
    }

    static void Check(bool cond, string message)
    {
        if (cond || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}
