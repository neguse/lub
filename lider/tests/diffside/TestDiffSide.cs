// The board showing the live version while the text differs from the version the game is running (side-by-side layout).
//   - Not shown while there is no difference. When typing creates a difference, it appears in front of and to the left of the focused window. The inspector window does not move
//   - The board aligns the line corresponding to the one near the cursor at the same height as in the window
//   - code:diff-side toggles between side-by-side and inline, and the board is not shown while inline
//   - When a reload is applied and the difference goes away, the board is removed
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestDiffSide
{
    static int frames;
    static bool failed;
    // Angle of the value tree board before typing, and the width (angle) between the live version board and the window
    static float boardBefore;
    static float oldSpan;

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
        App.Config(new ConfigOpts { Width = 1280, Height = 720 });
    }

    static void TestFrame(float dt)
    {
        if (failed)
            return;
        frames++;
        var w = Lider.Work!;
        if (frames == 2)
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
        // After lider places the window, advance 1 second until the window finishes expanding (do not rely on the progress made by the heavy frame that opened it)
        if (frames == 4)
            Settle(w);
        // Wait until the window has opened and finished moving (the angle of the value tree board is determined by the window's moving angle and size)
        if (frames < 60)
            return;
        var window = w.FocusWindow!;
        var doc = window.Code.Doc;
        var panel = Lider.Panel(window);
        if (frames == 60)
        {
            Check(!doc.Differs() && !panel.OldShown, "the board is shown before editing");
            boardBefore = Lider.BoardAngle;
            // Add one line below the '{' of Step (line 7)
            doc.MoveTo(6, Utf8.Bytes(doc.Text(6)));
            doc.Type("\n        var x = 0;");
            EditorHost.Step();
        }
        // After the live version board appears, advance until the board and the window finish moving into place
        if (frames == 61)
            Settle(w);
        if (frames == 62)
        {
            Check(doc.Differs() && panel.OldShown, "the board is not shown after typing");
            // The cursor is on the added line (which has no counterpart), so the '{' above it is aligned
            var lh = EditorHost.LineHeight(window.Code.ViewId);
            Check(Math.Abs(panel.OldTop(doc) + 6 * lh - EditorHost.LineY(window.Code.ViewId, 7)) < 0.5f,
                "the live line 7 is at " + (panel.OldTop(doc) + 6 * lh) + ", want " + EditorHost.LineY(window.Code.ViewId, 7));
            oldSpan = (PanelView.OldW * window.Scale + PanelView.OldGap) / Space.R;
            Check(Math.Abs(boardBefore - Lider.BoardAngle) < oldSpan * 0.01f, "the inspector moved by " + (boardBefore - Lider.BoardAngle));
            Lider.Run("code:diff-side");
        }
        if (frames == 64)
        {
            Check(!PanelView.SideBySide && !panel.OldShown, "code:diff-side did not hide the board");
            Check(Lider.DiffOf(window) == null && window.Children.Count == 0, "the hidden board is still a child of the window");
            Check(Math.Abs(Lider.BoardAngle - boardBefore) < oldSpan * 0.01f, "the inspector moved: " + Lider.BoardAngle + ", want " + boardBefore);
            Lider.Run("code:diff-side");
        }
        if (frames == 66)
        {
            Check(PanelView.SideBySide && panel.OldShown, "code:diff-side did not bring the board back");
            // The board is a child of the code window, is not in the screen's order, and sits in front of and to the left of its parent
            var board = Lider.DiffOf(window);
            Check(board != null && board.Parent == window && board.Content is Diff, "the board is not a child of the code window");
            Check(board != null && !w.ScreenOf(window)!.Windows.Contains(board), "the board is in the order of the screen");
            Check(board != null && board.Position!.Value.X < -window.UnscaledSize.X * window.Scale * 0.5f && board.Position.Value.Z < 0,
                "the board is not at the front left of its parent");
            doc.Save();
            doc.Reloaded();
        }
        if (frames == 68)
        {
            Check(!doc.Differs() && !panel.OldShown, "the board is still shown after the reload");
            if (!failed)
            {
                Console.WriteLine("PASS");
                App.Quit(0);
            }
        }
    }

    static void Press(int key)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = 0, Typed = 0, Plain = 0 });
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

    // Advance the window animation by 1 second
    static void Settle(Workspace w)
    {
        for (var i = 0; i < 60; i++)
            Lider.Animate(w, 1.0f / 60);
    }
}
